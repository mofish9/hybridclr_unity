
namespace HybridCLR
{
    public enum LoadImageErrorCode
	{
		OK = 0,
		BAD_IMAGE, // invalid dll file
        NOT_IMPLEMENT, // not implement feature
        AOT_ASSEMBLY_NOT_FIND, // AOT assembly not found
        HOMOLOGOUS_ONLY_SUPPORT_AOT_ASSEMBLY, // can not load supplementary metadata assembly for non-AOT assembly
        HOMOLOGOUS_ASSEMBLY_HAS_LOADED, // can not load supplementary metadata assembly for the same assembly
        INVALID_HOMOLOGOUS_MODE, // invalid homologous image mode
        PDB_BAD_FILE, // invalid pdb file
        UNKNOWN_IMAGE_FORMAT,
        UNSUPPORT_FORMAT_VERSION,
        UNMATCH_FORMAT_VARIANT,
		DHE_MV_BAD_FORMAT,
		DHE_MV_ASSEMBLY_NOT_FOUND,
		DHE_MV_REGISTRATION_FAILED,
		DHE_MV_CURRENT_HASH_MISMATCH,
		DHE_MV_BASELINE_HASH_MISMATCH,
		DHE_MV_BAD_SNAPSHOT_HASH,
		DHE_MV_DLL_ASSEMBLY_MISMATCH,
	};
}
