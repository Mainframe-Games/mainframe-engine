namespace MainframeEngine.Editor;

/// <summary>
/// The icon tint class for a node or resource type (<c>icon-3d</c>, <c>icon-2d</c>, <c>icon-ui</c>, <c>icon-audio</c>,
/// <c>icon-physics</c>, <c>icon-resource</c>, <c>icon-logic</c>). The most derived match wins, so a <c>RigidBody3D</c> is
/// physics and an <c>AudioPlayer3D</c> is audio, while other <c>Node3D</c>s are 3D.
/// </summary>
internal static class IconFamily
{
    public const string ThreeD = "icon-3d";
    public const string TwoD = "icon-2d";
    public const string Ui = "icon-ui";
    public const string Audio = "icon-audio";
    public const string Physics = "icon-physics";
    public const string Resource = "icon-resource";
    public const string Logic = "icon-logic";

    public static string Of(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        for (var t = type; t is not null; t = t.BaseType)
        {
            var family = t.Name switch
            {
                "UiLayer" or "UiDocument" or "Control" => Ui,
                "AudioPlayer" or "AudioPlayer2D" or "AudioPlayer3D" or "AudioListener2D" or "AudioListener3D" => Audio,
                "CollisionObject2D" or "CollisionObject3D" or "CollisionShape2D" or "CollisionShape3D" => Physics,
                "Node3D" => ThreeD,
                "Node2D" => TwoD,
                "Resource" => Resource,
                "Node" => Logic,
                _ => null,
            };
            if (family is not null)
                return family;
        }

        return Logic;
    }
}
