namespace MainframeEngine;

/// <summary>
/// Lets an <see cref="Animation"/>'s <see cref="MethodTrack"/> call this method (G1.4). The source generator emits a
/// typed invoker (no reflection) and a codec per parameter, so a key's JSON arguments are parsed once when the player
/// binds the animation. The method must be an instance method that is public or internal, non-generic, with
/// by-value parameters of types <c>[Export]</c> supports (resources excepted); its return value is ignored.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class AnimationMethodAttribute : Attribute;
