using Godot;

// One-shot, tiered explosion animation — GasSimulation instantiates this at
// an ignition's centroid and sets Tier BEFORE adding it to the tree (_Ready
// reads it to pick which of SpriteFrames' three animations to play).
// Self-frees on AnimationFinished, same "spawn, play, clean up" idiom every
// other one-shot VFX in this project already follows (BurnableComponent's
// SpawnBurnBurst, GasSimulation's own particle burst), just sprite-driven
// instead of code-built particles now that real animation frames exist.
public partial class GasExplosionEffect : Node2D
{
	public GasSimulation.ExplosionTier Tier = GasSimulation.ExplosionTier.Small;

	private AnimatedSprite2D _sprite;

	public override void _Ready()
	{
		_sprite = GetNode<AnimatedSprite2D>("AnimatedSprite2D");
		_sprite.AnimationFinished += OnAnimationFinished;
		_sprite.Play(Tier.ToString());
	}

	private void OnAnimationFinished()
	{
		QueueFree();
	}
}
