using Godot;

/// <summary>
/// Space parallax background controller.
///
/// Attach to a CanvasLayer node (layer = -10). Each direct Node2D child that
/// carries a "parallax_factor" metadata key (float 0.0–1.0) becomes one
/// parallax layer. The Sprite2D inside each layer is auto-scaled on startup
/// and on every viewport resize so it always covers the full screen even at
/// maximum camera drift — void can never appear, and nothing ever tiles.
///
/// parallax_factor guide:
///   0.00 → completely static (painted on the glass — infinitely far)
///   0.02 → barely drifts  (deep-space star field)
///   0.05 → subtle drift   (distant nebula / far planets)
///   0.09 → gentle drift   (mid-distance planet)
///   0.14 → noticeable     (closer planet / large object)
///   0.20 → most movement  (foreground dust / asteroids)
///
/// Tune MaxCameraTravel to match your level size. Making it larger than your
/// actual level width/height just means slightly more upscaling — it is safe
/// to overestimate.
/// </summary>
public partial class ParallaxBackgroundController : CanvasLayer
{
    /// <summary>
    /// Maximum distance (world units) the camera can travel from its starting
    /// position in any direction. Sprites are pre-sized so void never shows
    /// even if the camera reaches this limit. Default covers most platformers;
    /// increase for very large levels.
    /// </summary>
    [Export] public float MaxCameraTravel = 5000f;

    // Slow independent drift applied on top of camera parallax, scaled per
    // layer by its own parallax_factor (closer layers drift a bit more) —
    // keeps the backdrop feeling alive even while the camera holds still.
    // Comfortably inside the existing camera-travel padding at any factor,
    // so it never needs RefreshLayout to budget extra space for it.
    [Export] public float AmbientDriftAmplitude = 6f;
    [Export] public float AmbientDriftSpeed = 0.15f;

    private Camera2D _camera;
    private Vector2  _initialCameraPosition;
    private Vector2  _viewportCenter;
    private float    _ambientTime = 0f;

    // -------------------------------------------------------------------------
    //  Lifecycle
    // -------------------------------------------------------------------------

    public override void _Ready()
    {
        // Sit behind every other layer in the scene.
        Layer = -10;
        FollowViewportEnabled = false;

        // Capture the camera that exists at scene start.
        // If the camera spawns later, _Process will pick it up automatically.
        _camera = GetViewport().GetCamera2D();
        _initialCameraPosition = _camera?.GlobalPosition ?? Vector2.Zero;

        GetViewport().SizeChanged += OnViewportSizeChanged;
        RefreshLayout();
    }

    public override void _Process(double delta)
    {
        _ambientTime += (float)delta;

        // Lazy camera lookup — handles cameras that spawn after this node,
        // and re-checks even once a camera was already found: the cached
        // reference can be freed later (e.g. a multiplayer player
        // disconnecting, or a scene transition tearing down the previous
        // scene's camera) or superseded by a different one becoming current
        // (multiple player cameras during multiplayer spawn). Reading
        // GlobalPosition on a freed Camera2D throws ObjectDisposedException,
        // and a stale-but-still-alive reference to a camera that's no longer
        // "current" silently drifts the background out of sync with what's
        // actually on screen — so re-resolve whenever the cached one isn't
        // both alive and still the viewport's actual current camera.
        // A menu screen has no gameplay camera at all, ever — that's fine,
        // it just means travel stays zero; ambient drift and rotation below
        // don't depend on a camera existing and should keep animating there.
        if (_camera == null || !IsInstanceValid(_camera) || !_camera.Enabled)
        {
            _camera = GetViewport().GetCamera2D();
            if (_camera != null)
            {
                _initialCameraPosition = _camera.GlobalPosition;
            }
        }

        // How far has the camera moved from where it started?
        Vector2 travel = _camera != null ? _camera.GlobalPosition - _initialCameraPosition : Vector2.Zero;

        foreach (Node child in GetChildren())
        {
            if (child is Node2D layer && layer.HasMeta("parallax_factor"))
            {
                float factor = layer.GetMeta("parallax_factor").AsSingle();

                Vector2 ambientDrift = new Vector2(
                    Mathf.Sin(_ambientTime * AmbientDriftSpeed * Mathf.Tau),
                    Mathf.Cos(_ambientTime * AmbientDriftSpeed * 0.7f * Mathf.Tau) * 0.6f
                ) * AmbientDriftAmplitude * (factor / 0.2f);

                // Drift the layer in screen-space by a fraction of camera travel.
                //   factor = 0.00 → layer never moves  (stars feel infinitely far)
                //   factor = 0.20 → layer drifts 20 %  (feels much closer)
                //
                // Because sprites are sized to cover viewport + max drift, the
                // edges of any sprite are never visible no matter how far the
                // camera travels (up to MaxCameraTravel).
                layer.Position = _viewportCenter + travel * factor + ambientDrift;

                if (layer.HasMeta("rotation_speed"))
                {
                    float rotationSpeed = layer.GetMeta("rotation_speed").AsSingle();
                    layer.Rotation += Mathf.DegToRad(rotationSpeed) * (float)delta;
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    //  Layout helpers
    // -------------------------------------------------------------------------

    private void OnViewportSizeChanged() => RefreshLayout();

    /// <summary>
    /// Scales every managed sprite so it covers the full viewport even when
    /// the layer is at its maximum parallax offset. Called on startup and on
    /// every viewport resize (window resize, resolution change, etc.).
    /// </summary>
    private void RefreshLayout()
    {
        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        _viewportCenter  = viewport * 0.5f;

        foreach (Node child in GetChildren())
        {
            if (child is Node2D layer && layer.HasMeta("parallax_factor"))
            {
                float factor = layer.GetMeta("parallax_factor").AsSingle();

                // Maximum screen-space offset this layer can ever reach.
                // The sprite must extend this far beyond every viewport edge.
                float pad     = MaxCameraTravel * factor;
                float neededW = viewport.X + pad * 2f;
                float neededH = viewport.Y + pad * 2f;

                // Small speckle/noise textures (a distant dust field, say) look
                // like a blurred haze when stretched to cover the viewport —
                // tiling the source texture at its native resolution instead
                // keeps individual specks readable.
                bool tile = layer.HasMeta("tile") && layer.GetMeta("tile").AsBool();

                foreach (Node spriteNode in layer.GetChildren())
                {
                    if (spriteNode is Sprite2D sprite && sprite.Texture != null)
                    {
                        Vector2 texSize = sprite.Texture.GetSize();
                        if (texSize.X <= 0f || texSize.Y <= 0f) continue;

                        if (tile)
                        {
                            sprite.RegionEnabled = true;
                            sprite.RegionRect = new Rect2(-neededW * 0.5f, -neededH * 0.5f, neededW, neededH);
                            sprite.TextureRepeat = CanvasItem.TextureRepeatEnum.Enabled;
                            sprite.Scale = Vector2.One;
                        }
                        else
                        {
                            // Uniform scale: never stretch the texture, always cover.
                            float scale = Mathf.Max(neededW / texSize.X,
                                                    neededH / texSize.Y);

                            sprite.Scale = Vector2.One * scale;
                            sprite.TextureRepeat = CanvasItem.TextureRepeatEnum.Disabled;
                        }

                        sprite.Centered = true;        // centred on the layer origin
                        sprite.Position = Vector2.Zero; // layer origin IS screen centre
                    }
                }

                // Reset layer to viewport centre — _Process will offset it each frame.
                layer.Position = _viewportCenter;
            }
        }
    }
}