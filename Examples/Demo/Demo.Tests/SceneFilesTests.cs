using System.Text;
using MainframeEngine;

namespace Demo.Tests;

public sealed class SceneFilesTests
{
    private static string DemoRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void CommittedScenesMatchTheBuilders()
    {
        foreach (var scene in DemoScenes.All)
        {
            var root = scene.Build();
            try
            {
                var committed = File.ReadAllText(Path.Combine(DemoRoot, scene.Path)).ReplaceLineEndings();
                var uid = PackedSceneUid(committed);
                Assert.Equal(committed, Encoding.UTF8.GetString(SceneSaver.ToJson(root, uid)).ReplaceLineEndings());
            }
            finally
            {
                root.Free();
            }
        }
    }

    [Fact]
    public void SwitchingEveryTabTwiceLeavesOneScene()
    {
        var tree = new SceneTree(new ServerRegistry());
        foreach (var _ in Enumerable.Range(0, 2))
            foreach (var scene in DemoScenes.All)
            {
                tree.ChangeScene(scene.Build());
                Assert.Single(tree.Root.Children, c => DemoScenes.ByRootName(c.Name) is not null);
            }
    }

    private static string PackedSceneUid(string json) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("uid").GetString()!;
}
