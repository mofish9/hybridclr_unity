namespace __DHE_IDENTITY_NAMESPACE__
{
    // Staged only between scripts-only and final to bind immutable Base Player data.
    internal static class DheBuildIdentity
    {
        public const int IdentityVersion = 1;
        public const string Target = "";
        public const string EngineWorkflow = "";
        public const string Il2CppCodeGeneration = "";
        public const string AotSnapshotKind = "uninitialized-template";
        public const string BaseId = "0000000000000000000000000000000000000000000000000000000000000000";
        public const string ManagedAssemblySetSha256 = "0000000000000000000000000000000000000000000000000000000000000000";
        public const string AotAssemblySetSha256 = "0000000000000000000000000000000000000000000000000000000000000000";
        public const string AotSnapshotSha256 = "0000000000000000000000000000000000000000000000000000000000000000";
        public const string AotAnalysisSnapshotSha256 = "0000000000000000000000000000000000000000000000000000000000000000";
        public const string NativeGuardSourceSha256 = "0000000000000000000000000000000000000000000000000000000000000000";
        public const string NativeManifestSha256 = "0000000000000000000000000000000000000000000000000000000000000000";
        public const string BaseMetaVersionSetSha256 = "0000000000000000000000000000000000000000000000000000000000000000";
        public const string AotMetadataSetId = "0000000000000000000000000000000000000000000000000000000000000000";
        public const string RuntimeProtocol = "";
        public const string RuntimeContract = "";
        public const string RuntimeAssetRoot = "";
        public const string BaseMetaVersionAssetRoot = "";
        public static readonly string[] RuntimeCapabilities = new string[0];
        public static readonly string[] AotAssemblyNames = new string[0];
        public static readonly string[] AssemblyNames = new string[0];
        public static readonly string[] BaseMetaVersionHashes = new string[0];

        public static HybridCLR.DheRuntimeIdentity Create()
        {
            return new HybridCLR.DheRuntimeIdentity
            {
                IdentityVersion = IdentityVersion,
                Target = Target,
                EngineWorkflow = EngineWorkflow,
                Il2CppCodeGeneration = Il2CppCodeGeneration,
                AotSnapshotKind = AotSnapshotKind,
                BaseId = BaseId,
                ManagedAssemblySetSha256 = ManagedAssemblySetSha256,
                AotAssemblySetSha256 = AotAssemblySetSha256,
                AotSnapshotSha256 = AotSnapshotSha256,
                AotAnalysisSnapshotSha256 = AotAnalysisSnapshotSha256,
                NativeGuardSourceSha256 = NativeGuardSourceSha256,
                NativeManifestSha256 = NativeManifestSha256,
                BaseMetaVersionSetSha256 = BaseMetaVersionSetSha256,
                AotMetadataSetId = AotMetadataSetId,
                RuntimeProtocol = RuntimeProtocol,
                RuntimeContract = RuntimeContract,
                RuntimeCapabilities = RuntimeCapabilities,
                AotAssemblyNames = AotAssemblyNames,
                RuntimeAssetRoot = RuntimeAssetRoot,
                BaseMetaVersionAssetRoot = BaseMetaVersionAssetRoot,
                AssemblyNames = AssemblyNames,
                BaseMetaVersionHashes = BaseMetaVersionHashes,
            };
        }
    }
}
