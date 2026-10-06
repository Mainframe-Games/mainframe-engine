using System.Reflection;
using System.Runtime.CompilerServices;

namespace MainframeEngine;

/// <summary>
/// Godot's editor rule for scripts (ADR 0127): in <see cref="SceneTree.EditMode"/>, a node whose type
/// <see cref="SceneTree.EditModeScripts"/> calls a script (the editor: the game's code) and that is not
/// <see cref="ToolAttribute">[Tool]</see> skips its own <see cref="OnEnterTree"/>, <see cref="OnReady"/> and
/// <see cref="OnExitTree"/>: the implementation of its nearest non-script base type runs instead (a game Sprite2D
/// subclass still registers with the canvas), called non-virtually.
/// </summary>
public partial class Node
{
    private sealed class EngineBaseCallbacks
    {
        public nint EnterTree, Ready, ExitTree;
    }

    private static readonly ConditionalWeakTable<Type, EngineBaseCallbacks> EngineBases = new();

    // The predicate's answer for this node's type, remembered with the predicate it came from (per-draw hot path).
    private Func<Type, bool>? _scriptPredicate;
    private bool _isScript;

    /// <summary>True when this node's own lifecycle and draw callbacks are suppressed (edit mode, a non-tool script type).</summary>
    internal bool SkipsScriptCallbacks
    {
        get
        {
            if (_tree is not { EditMode: true, EditModeScripts: { } isScript } || _isTool)
                return false;
            if (!ReferenceEquals(isScript, _scriptPredicate))
            {
                _scriptPredicate = isScript;
                _isScript = isScript(GetType());
            }

            return _isScript;
        }
    }

    private unsafe void InvokeEnterTree()
    {
        if (SkipsScriptCallbacks)
            ((delegate* managed<Node, void>)BaseCallbacks().EnterTree)(this);
        else
            OnEnterTree();
    }

    private unsafe void InvokeReady()
    {
        if (SkipsScriptCallbacks)
            ((delegate* managed<Node, void>)BaseCallbacks().Ready)(this);
        else
            OnReady();
    }

    private unsafe void InvokeExitTree()
    {
        if (SkipsScriptCallbacks)
            ((delegate* managed<Node, void>)BaseCallbacks().ExitTree)(this);
        else
            OnExitTree();
    }

    private EngineBaseCallbacks BaseCallbacks()
    {
        var type = GetType();
        if (EngineBases.TryGetValue(type, out var found))
            return found;
        var isScript = _tree!.EditModeScripts!;
        var engine = type;
        while (engine.BaseType is { } baseType && isScript(engine))
            engine = baseType;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        // The implementation the non-script base type would run (its own override or an inherited one).
        static nint Pointer(Type t, string name) => t.GetMethod(name, flags, Type.EmptyTypes)!.MethodHandle.GetFunctionPointer();
        var callbacks = new EngineBaseCallbacks
        {
            EnterTree = Pointer(engine, nameof(OnEnterTree)),
            Ready = Pointer(engine, nameof(OnReady)),
            ExitTree = Pointer(engine, nameof(OnExitTree)),
        };
        EngineBases.AddOrUpdate(type, callbacks);
        return callbacks;
    }
}
