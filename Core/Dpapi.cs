using System.Runtime.InteropServices;
using System.Text;

namespace Vermilune.Core;

/// <summary>使用 Windows DPAPI 保护令牌，避免明文落盘。P/Invoke 版本不依赖额外 NuGet 包。</summary>
public static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr,
        ref DataBlob pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const int CryptProtectUiForbidden = 0x1;

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plain);
            var inBlob = new DataBlob();
            var outBlob = new DataBlob();
            var entropy = new DataBlob();
            inBlob.pbData = Marshal.AllocHGlobal(bytes.Length);
            inBlob.cbData = bytes.Length;
            Marshal.Copy(bytes, 0, inBlob.pbData, bytes.Length);
            try
            {
                if (!CryptProtectData(ref inBlob, "Vermilune", ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outBlob))
                    return "";
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return Convert.ToBase64String(result);
            }
            finally
            {
                if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("DPAPI 加密失败：" + ex.Message);
            return "";
        }
    }

    public static string Unprotect(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
            var bytes = Convert.FromBase64String(cipher);
            var inBlob = new DataBlob();
            var outBlob = new DataBlob();
            var entropy = new DataBlob();
            inBlob.pbData = Marshal.AllocHGlobal(bytes.Length);
            inBlob.cbData = bytes.Length;
            Marshal.Copy(bytes, 0, inBlob.pbData, bytes.Length);
            try
            {
                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outBlob))
                    return "";
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return Encoding.UTF8.GetString(result);
            }
            finally
            {
                if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("DPAPI 解密失败：" + ex.Message);
            return "";
        }
    }
}
