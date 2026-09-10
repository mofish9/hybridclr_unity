using System;
using System.IO;
using System.Text.RegularExpressions;

namespace HybridCLR.Editor.Commands
{
    // Only expand an already resolved MethodDef/MethodSpec symbol. Inline
    // copies have no independent entry in IL2CPP's method-pointer tables.
    public static class DheInlineGuardPolicy
    {
        public static void ValidateCopy(string primaryName, string primarySignature,
            string copyName, string copySignature)
        {
            if (!string.Equals(copyName, primaryName + "_inline", StringComparison.Ordinal) ||
                !copySignature.Contains("IL2CPP_MANAGED_FORCE_INLINE", StringComparison.Ordinal) ||
                CanonicalSignature(primaryName, primarySignature) != CanonicalSignature(copyName, copySignature))
                throw new InvalidDataException("DHE inline definition does not match its indexed native entry: " + copyName);
        }

        private static string CanonicalSignature(string function, string signature)
        {
            int attribute = signature.LastIndexOf("IL2CPP_METHOD_ATTR", StringComparison.Ordinal);
            if (attribute < 0) throw new InvalidDataException("DHE inline definition has no IL2CPP method attribute: " + function);
            string declaration = signature.Substring(attribute + "IL2CPP_METHOD_ATTR".Length).Trim();
            var name = Regex.Match(declaration, @"\b" + Regex.Escape(function) + @"(?=\s*\()");
            if (!name.Success) throw new InvalidDataException("DHE inline definition has no indexed function name: " + function);
            return Regex.Replace(declaration.Remove(name.Index, function.Length), @"\s+", "");
        }
    }
}
