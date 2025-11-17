using Godot;

public partial class Box_Big : RigidBody2D
{
    private CpuParticles2D _dustParticles;

    public override void _Ready()
    {
        _dustParticles = GetNode<CpuParticles2D>("DustParticles");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (LinearVelocity.Length() > 10f)
        {
            _dustParticles.Emitting = true;
            _dustParticles.Position = -LinearVelocity.Normalized() * 20;
        }
        else
        {
            _dustParticles.Emitting = false;
        }
    }
}