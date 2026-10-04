using System.Numerics;
using Silk.NET.Assimp;
using AssimpMesh = Silk.NET.Assimp.Mesh;

namespace SilkVulkanExamples.Vulkan;

/// <summary>
/// Loads a model using Assimp and creates VkMesh instances.
/// </summary>
public unsafe class VkModel : IDisposable
{
    private readonly ExampleBase _owner;
    public List<VkMesh> Meshes { get; } = [];

    public VkModel(ExampleBase owner, string path)
    {
        _owner = owner;
        var assimp = Assimp.GetApi();
        var scene = assimp.ImportFile(path, (uint)(PostProcessSteps.Triangulate | PostProcessSteps.GenerateNormals));

        if (scene == null || scene->MFlags == Assimp.SceneFlagsIncomplete || scene->MRootNode == null)
        {
            var error = assimp.GetErrorStringS();
            throw new InvalidOperationException($"Assimp error: {error}");
        }

        ProcessNode(scene->MRootNode, scene);
        assimp.Dispose();
    }

    private unsafe void ProcessNode(Node* node, Scene* scene)
    {
        for (uint i = 0; i < node->MNumMeshes; i++)
        {
            var mesh = scene->MMeshes[node->MMeshes[i]];
            Meshes.Add(ProcessMesh(mesh, scene));
        }
        for (uint i = 0; i < node->MNumChildren; i++)
            ProcessNode(node->MChildren[i], scene);
    }

    private unsafe VkMesh ProcessMesh(AssimpMesh* mesh, Scene* scene)
    {
        var vertices = new List<float>();
        var indices = new List<uint>();

        for (uint i = 0; i < mesh->MNumVertices; i++)
        {
            // Position
            vertices.Add(mesh->MVertices[i].X);
            vertices.Add(mesh->MVertices[i].Y);
            vertices.Add(mesh->MVertices[i].Z);
            // Normal
            if (mesh->MNormals != null)
            {
                vertices.Add(mesh->MNormals[i].X);
                vertices.Add(mesh->MNormals[i].Y);
                vertices.Add(mesh->MNormals[i].Z);
            }
            else
            {
                vertices.Add(0); vertices.Add(0); vertices.Add(0);
            }
            // UV
            if (mesh->MTextureCoords[0] != null)
            {
                Vector3 uv = mesh->MTextureCoords[0][i];
                vertices.Add(uv.X);
                vertices.Add(uv.Y);
            }
            else
            {
                vertices.Add(0); vertices.Add(0);
            }
        }

        for (uint i = 0; i < mesh->MNumFaces; i++)
        {
            var face = mesh->MFaces[i];
            for (uint j = 0; j < face.MNumIndices; j++)
                indices.Add(face.MIndices[j]);
        }

        return new VkMesh(_owner, vertices.ToArray(), indices.ToArray());
    }

    public void Dispose()
    {
        foreach (var mesh in Meshes)
            mesh.Dispose();
    }
}
