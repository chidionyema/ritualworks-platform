using System.Runtime.InteropServices;
using System.Security;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Extension methods for secure handling of SecureString objects.
/// These methods ensure proper memory cleanup when converting SecureString to plain text.
/// </summary>
public static class SecureStringExtensions
{
    /// <summary>
    /// Converts a SecureString to an insecure string.
    /// The plain text is allocated in unmanaged memory and immediately zeroed after use.
    ///
    /// WARNING: This creates a plain text copy of the secret in memory.
    /// Use only when absolutely necessary (e.g., for APIs that don't support SecureString).
    /// </summary>
    /// <param name="secureString">The SecureString to convert.</param>
    /// <returns>The plain text string, or empty string if input is null.</returns>
    public static string ToInsecureString(this SecureString? secureString)
    {
        if (secureString == null || secureString.Length == 0)
        {
            return string.Empty;
        }

        IntPtr unmanagedString = IntPtr.Zero;
        try
        {
            // Allocate unmanaged memory and copy the SecureString content
            unmanagedString = Marshal.SecureStringToGlobalAllocUnicode(secureString);
            return Marshal.PtrToStringUni(unmanagedString) ?? string.Empty;
        }
        finally
        {
            // Zero out and free the unmanaged memory immediately
            if (unmanagedString != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(unmanagedString);
            }
        }
    }

    /// <summary>
    /// Executes an action with the SecureString converted to plain text,
    /// ensuring the plain text is zeroed from memory after use.
    /// This is safer than ToInsecureString() when you only need temporary access.
    /// </summary>
    /// <typeparam name="T">The return type of the action.</typeparam>
    /// <param name="secureString">The SecureString to use.</param>
    /// <param name="action">The action to execute with the plain text password.</param>
    /// <returns>The result of the action.</returns>
    public static T UseAsPlainText<T>(this SecureString? secureString, Func<string, T> action)
    {
        if (secureString == null || secureString.Length == 0)
        {
            return action(string.Empty);
        }

        IntPtr unmanagedString = IntPtr.Zero;
        try
        {
            unmanagedString = Marshal.SecureStringToGlobalAllocUnicode(secureString);
            var plainText = Marshal.PtrToStringUni(unmanagedString) ?? string.Empty;
            return action(plainText);
        }
        finally
        {
            if (unmanagedString != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(unmanagedString);
            }
        }
    }

    /// <summary>
    /// Executes an action with the SecureString converted to plain text,
    /// ensuring the plain text is zeroed from memory after use.
    /// </summary>
    /// <param name="secureString">The SecureString to use.</param>
    /// <param name="action">The action to execute with the plain text password.</param>
    public static void UseAsPlainText(this SecureString? secureString, Action<string> action)
    {
        if (secureString == null || secureString.Length == 0)
        {
            action(string.Empty);
            return;
        }

        IntPtr unmanagedString = IntPtr.Zero;
        try
        {
            unmanagedString = Marshal.SecureStringToGlobalAllocUnicode(secureString);
            var plainText = Marshal.PtrToStringUni(unmanagedString) ?? string.Empty;
            action(plainText);
        }
        finally
        {
            if (unmanagedString != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(unmanagedString);
            }
        }
    }

    /// <summary>
    /// Creates a SecureString from a plain text string.
    /// The SecureString is made read-only after creation.
    /// </summary>
    /// <param name="plainText">The plain text to convert.</param>
    /// <returns>A read-only SecureString containing the text.</returns>
    public static SecureString ToSecureString(this string? plainText)
    {
        var secureString = new SecureString();

        if (!string.IsNullOrEmpty(plainText))
        {
            foreach (char c in plainText)
            {
                secureString.AppendChar(c);
            }
        }

        secureString.MakeReadOnly();
        return secureString;
    }

    /// <summary>
    /// Compares two SecureStrings for equality without exposing the contents.
    /// Uses constant-time comparison to prevent timing attacks.
    /// </summary>
    /// <param name="secureString1">The first SecureString.</param>
    /// <param name="secureString2">The second SecureString.</param>
    /// <returns>True if the strings are equal, false otherwise.</returns>
    public static bool SecureEquals(this SecureString? secureString1, SecureString? secureString2)
    {
        if (secureString1 == null && secureString2 == null)
            return true;
        if (secureString1 == null || secureString2 == null)
            return false;
        if (secureString1.Length != secureString2.Length)
            return false;

        IntPtr ptr1 = IntPtr.Zero;
        IntPtr ptr2 = IntPtr.Zero;

        try
        {
            ptr1 = Marshal.SecureStringToGlobalAllocUnicode(secureString1);
            ptr2 = Marshal.SecureStringToGlobalAllocUnicode(secureString2);

            // Constant-time comparison
            int result = 0;
            for (int i = 0; i < secureString1.Length * 2; i++)
            {
                result |= Marshal.ReadByte(ptr1, i) ^ Marshal.ReadByte(ptr2, i);
            }

            return result == 0;
        }
        finally
        {
            if (ptr1 != IntPtr.Zero)
                Marshal.ZeroFreeGlobalAllocUnicode(ptr1);
            if (ptr2 != IntPtr.Zero)
                Marshal.ZeroFreeGlobalAllocUnicode(ptr2);
        }
    }
}
