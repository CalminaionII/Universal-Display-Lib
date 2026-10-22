using Vintagestory.API.Client;

namespace UniversalDisplayLib;

internal static class GhostMeshUpload
{
    // Liquid-container chunk meshes carry CustomInts/CustomFloats without a GPU
    // attribute layout. The chunk builder supplies that layout later; standalone
    // UploadMesh dereferences it directly. Standard-shader ghosts do not use these
    // incomplete chunk-only attributes. Never change the BE's shared source mesh.
    public static MeshData Prepare(MeshData mesh)
    {
        bool floats = mesh.CustomFloats != null && mesh.CustomFloats.InterleaveSizes == null;
        bool ints = mesh.CustomInts != null && mesh.CustomInts.InterleaveSizes == null;
        bool shorts = mesh.CustomShorts != null && mesh.CustomShorts.InterleaveSizes == null;
        bool bytes = mesh.CustomBytes != null && mesh.CustomBytes.InterleaveSizes == null;
        if (!floats && !ints && !shorts && !bytes) return mesh;
        MeshData copy = mesh.Clone();
        if (floats) copy.CustomFloats = null;
        if (ints) copy.CustomInts = null;
        if (shorts) copy.CustomShorts = null;
        if (bytes) copy.CustomBytes = null;
        return copy;
    }
}
