using MemoryMcp.Infrastructure.Persistence;

namespace MemoryMcp.Infrastructure.Tests;

internal static class TestVectors
{
    /// <summary>Builds a full-width (VectorSettings.Dimensions) embedding with the given leading values, zero-padded.</summary>
    public static float[] Embedding(params float[] head)
    {
        var vector = new float[VectorSettings.Dimensions];
        head.CopyTo(vector, 0);
        return vector;
    }
}
