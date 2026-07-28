using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using Echo.Core;

namespace Echo;

internal sealed class SearchSweeper : IDisposable
{
	private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(7L);

	private const float MoveEpsilon = 0.5f;

	private readonly IFramework _framework;

	private readonly IObjectTable _objectTable;

	private readonly ICondition _condition;

	private readonly PluginState _state;

	private readonly IPluginLog _log;

	private readonly SearchSweepPlan _plan;

	private Vector3? _lastPos;

	private DateTime _lastMovedAt = DateTime.UtcNow;

	private bool _guardTripped;

	private bool _active;

	private FireResult? _lastFire;

	private bool _errorWarned;

	public SearchSweeper(IFramework framework, IObjectTable objectTable, ICondition condition, PluginState state, IPluginLog log)
	{
		_framework = framework;
		_objectTable = objectTable;
		_condition = condition;
		_state = state;
		_log = log;
		_plan = new SearchSweepPlan(SearchJobBits.SearchableJobIds, new Random());
	}

	public void Enable()
	{
		_framework.Update += OnUpdate;
	}

	public void Dispose()
	{
		_framework.Update -= OnUpdate;
	}

	private void OnUpdate(IFramework _)
	{
		try
		{
			if (_guardTripped)
			{
				return;
			}
			PluginStateSnapshot snap = _state.Snapshot();
			if (!snap.CaptureEnabled || !snap.ServerAllowsIngest || !snap.AutoSearchEnabled || !snap.SearchCaptureEnabled || snap.Standby)
			{
				_active = false;
				return;
			}
			if (!_active)
			{
				_active = true;
				_plan.Reset();
				_lastFire = null;
				_log.Info("Echo auto-search sweep active (fires while idle in a safe zone)");
			}
			DateTimeOffset now = DateTimeOffset.UtcNow;
			bool gatesOpen = EvaluateGate(now);
			SweepAction action = _plan.Advance(now, gatesOpen);
			switch (action.Step)
			{
			case SweepStep.Fire:
			{
				FireResult result = PlayerSearchRequest.Fire(action.Target, _log);
				if (result != FireResult.Sent)
				{
					_plan.CancelPendingSample();
				}
				if (result == FireResult.StructMismatch)
				{
					_guardTripped = true;
					break;
				}
				if (result != _lastFire)
				{
					IPluginLog log = _log;
					log.Info(result switch
					{
						FireResult.Sent => $"Echo auto-search sending searches (job {action.Target.JobId} lv {action.Target.LevelMin}-{action.Target.LevelMax})", 
						FireResult.NotSent => "Echo auto-search: the client declined the search request; retrying on the normal interval", 
						_ => "Echo auto-search: search proxy unavailable; retrying on the normal interval", 
					});
				}
				else if (result == FireResult.Sent)
				{
					_log.Verbose($"Echo auto-search fired job {action.Target.JobId} lv {action.Target.LevelMin}-{action.Target.LevelMax}");
				}
				_lastFire = result;
				break;
			}
			case SweepStep.Sample:
				var (count, sampleJob) = PlayerSearchRequest.ReadResultSummary();
				_log.Verbose($"Echo auto-search job {action.Target.JobId} returned {count}, sample job={sampleJob}");
				_plan.ReportResultCount(count, action.Target);
				break;
			}
		}
		catch (Exception exception)
		{
			if (!_errorWarned)
			{
				_errorWarned = true;
				_log.Warning(exception, "Echo auto-search sweep failed (repeats log at Verbose)");
			}
			else
			{
				_log.Verbose(exception, "Echo auto-search sweep failed");
			}
		}
	}

	private bool EvaluateGate(DateTimeOffset now)
	{
		IPlayerCharacter local = _objectTable.LocalPlayer;
		if (local == null)
		{
			_lastPos = null;
			return false;
		}
		Vector3 pos = local.Position;
		Vector3? lastPos = _lastPos;
		if (lastPos.HasValue)
		{
			Vector3 prev = lastPos.GetValueOrDefault();
			if (!(Vector3.Distance(pos, prev) > 0.5f))
			{
				goto IL_0057;
			}
		}
		_lastMovedAt = now.UtcDateTime;
		goto IL_0057;
		IL_0057:
		_lastPos = pos;
		if (_condition[ConditionFlag.InCombat] || _condition[ConditionFlag.BoundByDuty] || _condition[ConditionFlag.BoundByDuty56] || _condition[ConditionFlag.BoundByDuty95] || _condition[ConditionFlag.InDeepDungeon] || _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51] || _condition[ConditionFlag.OccupiedInCutSceneEvent] || _condition[ConditionFlag.WatchingCutscene] || _condition[ConditionFlag.LoggingOut])
		{
			return false;
		}
		return now.UtcDateTime - _lastMovedAt >= IdleAfter;
	}
}
