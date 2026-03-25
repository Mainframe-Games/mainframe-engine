using System.Numerics;

namespace MainframeEngine;

public class SceneGrid3d : SceneGrid
{
    public unsafe SceneGrid3d(IRenderer renderer, uint gridSize = 200)
        : base(renderer, (gridSize + 1) * 6)
    {
        var gridSizeHalf = (int)gridSize / 2;
        var vertices = stackalloc Vertex[(int)_vertexCount];
        var vIndex = 0;

        // x axis - red
        for (int x = gridSizeHalf; x >= -gridSizeHalf; --x)
        {
            var c = x == 0 ? Red : new Vector4(1, 1, 1, 0.2f);
            vertices[vIndex++] = new Vertex(gridSizeHalf, 0, -x, c);
            vertices[vIndex++] = new Vertex(-gridSizeHalf, 0, -x, c);
        }

        // y axis - yellow
        {
            vertices[vIndex++] = new Vertex(0, gridSizeHalf, 0, Yellow);
            vertices[vIndex++] = new Vertex(0, -gridSizeHalf, 0, Yellow);
        }

        // z axis - blue
        for (int z = gridSizeHalf; z >= -gridSizeHalf; --z)
        {
            var c = z == 0 ? Blue : DefaultColor;
            vertices[vIndex++] = new Vertex(-z, 0, -gridSizeHalf, c);
            vertices[vIndex++] = new Vertex(-z, 0, gridSizeHalf, c);
        }

        BuildVertexArray(vertices);
    }
}
