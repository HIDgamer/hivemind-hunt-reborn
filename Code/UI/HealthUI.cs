using Godot;
using System;

public partial class HealthUI : CanvasLayer
{
    [Export] public Sam Player; // Reference to the player, set in the scene

    private Sprite2D _healthBar;

    public override void _Ready()
    {
        _healthBar = GetNode<Sprite2D>("Sprite2D");
    }

    public override void _Process(double delta)
    {
        if (Player != null)
        {
            UpdateHealthBar();
        }
    }

    private void UpdateHealthBar()
    {
        int currentHealth = Player.GetHealth();
        int maxHealth = 200; // Matches player's MaxHealth
        int frame;

        if (currentHealth <= 0)
        {
            frame = 10; // Empty health bar (dead)
        }
        else
        {
            // Each slot represents 20 health (200 / 10 slots)
            frame = Mathf.FloorToInt((maxHealth - currentHealth) / 20f);
            frame = Mathf.Clamp(frame, 0, 9); // Frames 0-9 for health levels
        }

        _healthBar.Frame = frame;
    }
}