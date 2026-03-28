using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Windowing;

namespace MainframeEngine;

/// <summary>
/// Contains all relevant callbacks for game loop.
/// </summary>
public interface IGame
{
    /// <summary>
    /// Initializes game-specific resources or performs necessary setup tasks prior to the game loop starting.
    /// </summary>
    /// <param name="window">
    /// The engine's primary window instance, used for setting up rendering context,
    /// input handling, or other game initialization routines.
    /// </param>
    void OnLoad(in Engine window);

    /// <summary>
    /// Handles window size changes and adjusts necessary components or viewport settings accordingly.
    /// </summary>
    /// <param name="newSize">
    /// The new size of the window, represented as a 2D vector (width and height).
    /// </param>
    void OnResize(in Vector2 newSize);

    /// <summary>
    /// Renders ImGui elements, providing a frame-specific UI context for debugging, visualization,
    /// or runtime interaction.
    /// </summary>
    /// <param name="gameTime">
    /// The current frame's timing information including frame count, delta time, frames per second, and frame time in milliseconds.
    /// </param>
    void OnImGui(in GameTime gameTime);

    /// <summary>
    /// Updates the game's state, including applying game logic and handling frame-specific updates.
    /// </summary>
    /// <param name="gameTime">
    /// Timing information for the current frame such as elapsed time since the last frame,
    /// total frame count, and frame rate metrics.
    /// </param>
    void OnUpdate(in GameTime gameTime);

    /// <summary>
    /// Called before the main render pass begins. Use this to render shadow maps or other pre-pass work.
    /// The command buffer is active but no render pass has started yet.
    /// </summary>
    void OnShadowPass(in GameTime gameTime) { }

    /// <summary>
    /// Renders the current frame, including 3D objects, UI elements, and other visual components.
    /// </summary>
    /// <param name="gameTime">
    /// Contains timing details for the current frame, such as frame count, delta time, frame rate, and frame duration in milliseconds.
    /// </param>
    void OnRender(in GameTime gameTime);

    /// <summary>
    /// Executes necessary cleanup operations and resource disposal when the game is closing.
    /// </summary>
    void OnClose();
}
