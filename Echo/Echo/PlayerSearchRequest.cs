using System;
using System.Runtime.CompilerServices;
using Dalamud.Plugin.Services;
using Echo.Core;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace Echo;

internal static class PlayerSearchRequest
{
	private const int ExpectedSize = 400;

	private const int OffLocationCount = 208;

	private const int OffJobMask = 216;

	private const int OffLevelMin = 224;

	private const int OffLevelMax = 226;

	private const int OffGrandCompanyMask = 232;

	private const int OffLanguageMask = 240;

	private const int OffOnlineStatusMask = 248;

	private const int OffLocationIds = 256;

	private const int OffName = 356;

	private const int OffLocationGroups = 386;

	private static readonly ushort[] AllSearchAreas = new ushort[34]
	{
		500, 502, 501, 506, 507, 504, 505, 503, 512, 508,
		510, 4136, 511, 4137, 514, 513, 515, 3745, 3741, 4138,
		3744, 3740, 4515, 4511, 4512, 4514, 516, 517, 518, 509,
		3742, 3743, 4513, 519
	};

	public unsafe static FireResult Fire(SweepTarget target, IPluginLog log)
	{
		if (Unsafe.SizeOf<InfoProxySearch>() != 400)
		{
			log.Warning($"Echo auto-search disabled: InfoProxySearch size 0x{Unsafe.SizeOf<InfoProxySearch>():X} != 0x{400:X}");
			return FireResult.StructMismatch;
		}
		InfoModule* module = InfoModule.Instance();
		if (module == null)
		{
			return FireResult.ProxyUnavailable;
		}
		InfoProxySearch* proxy = (InfoProxySearch*)module->GetInfoProxyById(InfoProxyId.PlayerSearch);
		if (proxy == null)
		{
			return FireResult.ProxyUnavailable;
		}
		byte* p = (byte*)proxy;
		ushort* locs = (ushort*)p + 128;
		if (*locs == 0)
		{
			for (int i = 0; i < AllSearchAreas.Length; i++)
			{
				locs[i] = AllSearchAreas[i];
			}
			p[386] = 7;
			log.Info("Echo auto-search: armed the search-area list for this session");
		}
		p[208] = 0;
		((long*)p)[27] = (long)SearchJobBits.MaskFor(target.JobId);
		((short*)p)[112] = (short)target.LevelMin;
		((short*)p)[113] = (short)target.LevelMax;
		p[232] = byte.MaxValue;
		p[240] = byte.MaxValue;
		((long*)p)[31] = 0L;
		p[356] = 0;
		bool sent = ((InfoProxyInterface*)proxy)->RequestData();
		log.Verbose($"Echo auto-search RequestData sent={sent} job {target.JobId} lv {target.LevelMin}-{target.LevelMax}");
		if (!sent)
		{
			return FireResult.NotSent;
		}
		return FireResult.Sent;
	}

	public unsafe static (int Count, byte SampleJob) ReadResultSummary()
	{
		InfoModule* module = InfoModule.Instance();
		if (module == null)
		{
			return (Count: 0, SampleJob: 0);
		}
		InfoProxyCommonList* proxy = (InfoProxyCommonList*)module->GetInfoProxyById(InfoProxyId.PlayerSearch);
		if (proxy == null)
		{
			return (Count: 0, SampleJob: 0);
		}
		int count = 0;
		byte sampleJob = 0;
		ReadOnlySpan<InfoProxyCommonList.CharacterData> charDataSpan = proxy->CharDataSpan;
		for (int i = 0; i < charDataSpan.Length; i++)
		{
			ref readonly InfoProxyCommonList.CharacterData d = ref charDataSpan[i];
			if (d.ContentId != 0L)
			{
				if (count == 0)
				{
					sampleJob = d.Job;
				}
				count++;
			}
		}
		return (Count: count, SampleJob: sampleJob);
	}
}
