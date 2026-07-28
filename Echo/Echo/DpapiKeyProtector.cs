using System.Security.Cryptography;
using Echo.Core;

namespace Echo;

public sealed class DpapiKeyProtector : IKeyProtector
{
	private static readonly byte[] Entropy = "echo-keystore-v1"u8.ToArray();

	public byte[] Protect(byte[] data)
	{
		return ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
	}

	public byte[] Unprotect(byte[] data)
	{
		return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
	}
}
