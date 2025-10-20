using Godot;

public partial class CrosshairHud : Control
{
	[Export] public Color CrosshairColor = new Color(1, 1, 1, 0.85f);
	[Export] public int LineLen = 8;
	[Export] public int LineThickness = 2;
	[Export] public int Gap = 4;

	public void SetActive(bool on)
	{
		Visible = on;
		QueueRedraw();
	}

	public override void _Ready()
	{
		AnchorsPreset = (int)LayoutPreset.FullRect;
		MouseFilter = MouseFilterEnum.Ignore;
		Visible = false;
	}

	public override void _Draw()
	{
		if (!Visible) return;

		Vector2 c = Size * 0.5f;

		DrawLine(c + new Vector2(Gap, 0), c + new Vector2(Gap + LineLen, 0), CrosshairColor, LineThickness);
		DrawLine(c - new Vector2(Gap, 0), c - new Vector2(Gap + LineLen, 0), CrosshairColor, LineThickness);
		DrawLine(c + new Vector2(0, Gap), c + new Vector2(0, Gap + LineLen), CrosshairColor, LineThickness);
		DrawLine(c - new Vector2(0, Gap), c - new Vector2(0, Gap + LineLen), CrosshairColor, LineThickness);
	}

	public override void _Process(double delta)
	{
		if (Visible) QueueRedraw();
	}
}
