using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HybridCLR
{
    /// <summary>Compiler-produced selections for one immutable Base/Current MV pair.</summary>
    [Serializable]
    public sealed class DheExecutionPlan
    {
        public const string Capability = "current-storage-execution-plan-v1";
        public int schemaVersion;
        public string assemblyName;
        public string baseMetaVersionSha256;
        public string currentMetaVersionSha256;
        public uint[] currentStorageTypeTokens;
        public uint[] currentExecutionMethodTokens;

        /// <summary>Canonical representation used when comparing manifest, validation and runtime plan.</summary>
        public string CanonicalBinding()
        {
            if (schemaVersion != 1 || string.IsNullOrWhiteSpace(assemblyName) ||
                assemblyName.IndexOfAny(new[] { '\r', '\n', '|', '/', '\\' }) >= 0 ||
                !IsHash(baseMetaVersionSha256) || !IsHash(currentMetaVersionSha256))
                throw new InvalidDataException("DHE execution plan identity is invalid.");
            ValidateTokens(currentStorageTypeTokens, 2, 1);
            ValidateTokens(currentExecutionMethodTokens, 6, 0);
            return assemblyName + "|" + baseMetaVersionSha256.ToUpperInvariant() + "|" +
                currentMetaVersionSha256.ToUpperInvariant() + "|" +
                string.Join(",", currentStorageTypeTokens.Select(token => token.ToString("X8"))) + "|" +
                string.Join(",", currentExecutionMethodTokens.Select(token => token.ToString("X8")));
        }

        /// <summary>Checks the exact embedded Base and downloaded Current before any native mutation.</summary>
        public void Validate(string expectedAssemblyName, byte[] baseMetaVersion, byte[] currentMetaVersion)
        {
            CanonicalBinding();
            if (!string.Equals(assemblyName, expectedAssemblyName, StringComparison.Ordinal) ||
                !string.Equals(Hash(baseMetaVersion), baseMetaVersionSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Hash(currentMetaVersion), currentMetaVersionSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("DHE execution plan MV binding mismatch: " + expectedAssemblyName);
            var before = ReadMetaVersion(baseMetaVersion);
            var after = ReadMetaVersion(currentMetaVersion);
            var baseTypes = before.Types.Values.ToDictionary(member => member.StableId, StringComparer.Ordinal);
            var baseMethods = before.Methods.Values.ToDictionary(member => member.StableId, StringComparer.Ordinal);
            foreach (uint token in currentStorageTypeTokens)
                ValidateMember(token, baseTypes, after.Types, false);
            foreach (uint token in currentExecutionMethodTokens)
                ValidateMember(token, baseMethods, after.Methods, true);
        }

        private static void ValidateTokens(uint[] tokens, uint table, uint minimumRow)
        {
            if (tokens == null) throw new InvalidDataException("DHE execution plan selection array is missing.");
            uint previous = 0;
            foreach (uint token in tokens)
            {
                if ((token >> 24) != table || (token & 0xffffffu) <= minimumRow || token <= previous)
                    throw new InvalidDataException("DHE execution plan tokens must be valid, unique and sorted.");
                previous = token;
            }
        }

        private sealed class Member
        {
            internal string StableId;
            internal string Owner;
            internal uint Flags;
        }

        private sealed class MetaVersion
        {
            internal readonly Dictionary<uint, Member> Types = new Dictionary<uint, Member>();
            internal readonly Dictionary<uint, Member> Methods = new Dictionary<uint, Member>();
        }

        private MetaVersion ReadMetaVersion(byte[] bytes)
        {
            if (bytes.Length < 60 || Encoding.ASCII.GetString(bytes, 0, 8) != "DHEMETA1" ||
                BitConverter.ToUInt32(bytes, 8) != 1)
                throw new InvalidDataException("DHE execution plan MV format is invalid.");
            uint nameLength = BitConverter.ToUInt32(bytes, 16), typeCount = BitConverter.ToUInt32(bytes, 20),
                methodCount = BitConverter.ToUInt32(bytes, 24);
            long expectedSize = 60L + nameLength + 72L * typeCount + 104L * methodCount;
            if (expectedSize != bytes.Length ||
                new UTF8Encoding(false, true).GetString(bytes, 60, checked((int)nameLength)) != assemblyName)
                throw new InvalidDataException("DHE execution plan MV assembly or length is invalid.");
            var result = new MetaVersion();
            int offset = checked(60 + (int)nameLength);
            for (int table = 0; table < 2; table++)
            {
                var target = table == 0 ? result.Types : result.Methods;
                uint count = table == 0 ? typeCount : methodCount;
                int tokenOffset = table == 0 ? 64 : 96, width = table == 0 ? 72 : 104;
                var ids = new HashSet<string>(StringComparer.Ordinal);
                for (uint index = 0; index < count; index++, offset += width)
                {
                    var member = new Member { StableId = Convert.ToBase64String(bytes, offset, 32),
                        Owner = table == 0 ? null : Convert.ToBase64String(bytes, offset + 64, 32),
                        Flags = BitConverter.ToUInt32(bytes, offset + tokenOffset + 4) };
                    uint token = BitConverter.ToUInt32(bytes, offset + tokenOffset);
                    if (!ids.Add(member.StableId) || target.ContainsKey(token))
                        throw new InvalidDataException("DHE execution plan MV has duplicate members.");
                    target.Add(token, member);
                }
            }
            return result;
        }

        private static void ValidateMember(uint token, Dictionary<string, Member> before,
            Dictionary<uint, Member> after, bool method)
        {
            if (!after.TryGetValue(token, out Member current))
                throw new InvalidDataException("DHE execution plan token is absent from Current MV.");
            if (!before.TryGetValue(current.StableId, out Member original) || original.Owner != current.Owner ||
                (method ? !CanExecute(original.Flags) || !CanExecute(current.Flags) : original.Flags != current.Flags))
                throw new InvalidDataException("DHE execution plan member has no compatible Base entry.");
        }

        private static bool CanExecute(uint flags) => (flags & 8u) != 0 && (flags & (2u | 4u)) == 0;
        private static bool IsHash(string value) => value != null && value.Length == 64 &&
            value.All(character => character >= '0' && character <= '9' || character >= 'a' && character <= 'f' ||
                character >= 'A' && character <= 'F');
        private static string Hash(byte[] bytes)
        {
            if (bytes == null) throw new InvalidDataException("DHE execution plan MV bytes are missing.");
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
        }
    }
}
