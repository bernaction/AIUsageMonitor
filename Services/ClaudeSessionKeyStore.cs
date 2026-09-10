using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AIUsageMonitor.Services;

public sealed class ClaudeSessionKeyStore
{
    private const string CredentialTarget = "AIUsageMonitor/ClaudeWebSessionKey";
    private const int ErrorNotFound = 1168;

    public bool IsConfigured => TryRead(out _);

    public string? Read() => TryRead(out string? sessionKey) ? sessionKey : null;

    public void Save(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);

        byte[] secretBytes = Encoding.Unicode.GetBytes(sessionKey);
        IntPtr secretPointer = Marshal.AllocCoTaskMem(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, secretPointer, secretBytes.Length);
            NativeCredential credential = new()
            {
                Type = CredentialType.Generic,
                TargetName = CredentialTarget,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = secretPointer,
                Persist = CredentialPersistence.LocalMachine,
                UserName = Environment.UserName
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Array.Clear(secretBytes);
            Marshal.Copy(secretBytes, 0, secretPointer, secretBytes.Length);
            Marshal.FreeCoTaskMem(secretPointer);
        }
    }

    public void Delete()
    {
        if (!CredDelete(CredentialTarget, CredentialType.Generic, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error);
            }
        }
    }

    private static bool TryRead(out string? sessionKey)
    {
        sessionKey = null;
        if (!CredRead(CredentialTarget, CredentialType.Generic, 0, out IntPtr credentialPointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return false;
            }
            throw new Win32Exception(error);
        }

        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return false;
            }

            sessionKey = Marshal.PtrToStringUni(
                credential.CredentialBlob,
                checked((int)credential.CredentialBlobSize / sizeof(char)));
            return !string.IsNullOrWhiteSpace(sessionKey);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        CredentialType type,
        uint reservedFlag,
        out IntPtr credentialPointer);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, CredentialType type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr credentialPointer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public CredentialType Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public CredentialPersistence Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    private enum CredentialType : uint
    {
        Generic = 1
    }

    private enum CredentialPersistence : uint
    {
        LocalMachine = 2
    }
}
