namespace MainframeEngine;

public class SceneGrid2d : SceneGrid
{
    public unsafe SceneGrid2d(IRenderer renderer, uint gridSize = 200)
        : base(renderer, (gridSize + 1) * 4)
    {
        var gridSizeHalf = (int)gridSize / 2;
        var vertices = stackalloc Vertex[(int)_vertexCount];
        var vIndex = 0;

        // x axis - red
        for (int x = gridSizeHalf; x >= -gridSizeHalf; --x)
        {
            var c = x == 0 ? Red : DefaultColor;
            vertices[vIndex++] = new Vertex(gridSizeHalf, -x, 0, c);
            vertices[vIndex++] = new Vertex(-gridSizeHalf, -x, 0, c);
        }

        // y axis - yellow
        for (int y = gridSizeHalf; y >= -gridSizeHalf; --y)
        {
            var c = y == 0 ? Yellow : DefaultColor;
            vertices[vIndex++] = new Vertex(-y, -gridSizeHalf, 0, c);
            vertices[vIndex++] = new Vertex(-y, gridSizeHalf, 0, c);
        }

        BuildVertexArray(vertices);
    }
}
