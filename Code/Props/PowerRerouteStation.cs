using Godot;

// Interact to solve the Power Reroute minigame and bring every Light2D and
// LaserTurret tagged with SectionGroup back online. Group-based rather than
// a fixed NodePath list — a level designer just adds every light/turret in
// a dead section to one group name and points this station at it.
public partial class PowerRerouteStation : TaskStationBase
{
	[Export] public string SectionGroup = "";

	protected override void OnSolved()
	{
		if (string.IsNullOrEmpty(SectionGroup)) return;

		foreach (Node node in GetTree().GetNodesInGroup(SectionGroup))
		{
			if (node is PowerLightFixture fixture) fixture.Powered(true);
			else if (node is Light2D light) light.Enabled = true;
			else if (node is LaserTurret turret) turret.Powered(true);
		}
	}
}
