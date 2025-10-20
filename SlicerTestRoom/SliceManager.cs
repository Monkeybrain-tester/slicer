// File: SliceManager.cs
using Godot;
using System;
using System.Linq;
using System.Collections.Generic;

public partial class SliceManager : Node3D
{
	// -----------------------------
	// Scene links / targets
	// -----------------------------
	[ExportGroup("Scene Links")]
	[Export] public NodePath PlayerPath;
	[Export] public NodePath RoomRootPath;
	[Export] public NodePath[] TargetMeshes;
	[Export] public NodePath CrosshairPath;

	// -----------------------------
	// Projected Guide (Decal)
	// -----------------------------
	[ExportGroup("Projected Guide")]
	[Export] public bool UseProjectedGuide = true;
	[Export] public Texture2D GuideTexture;
	[Export] public Color GuideTint = new Color(0.9f, 0.5f, 1.0f, 1.0f);
	[Export] public float GuideHeight = 6.0f;
	[Export] public float GuideThickness = 0.25f;
	[Export] public float MaxPreviewDistance = 200.0f;

	// Optional translucent plane (debug)
	[ExportGroup("Legacy Plane Preview")]
	[Export] public bool ShowPreviewPlane = false;
	[Export] public Color PreviewColor = new Color(0.9f, 0.2f, 0.2f, 0.25f);

	// Slice visuals & behavior
	[ExportGroup("Slice Visuals")]
	[Export] public bool ShowCap = false;
	[Export] public Material CapMaterial;

	[ExportGroup("Slice Behavior")]
	[Export] public bool KeepBackHalf = false;
	[Export] public bool ForceVertical = true;

	// -----------------------------
	// Private state
	// -----------------------------
	private CharacterBody3D _player;
	private Node _roomRoot;
	private Control _crosshair;
	private Camera3D _cam;

	private Decal _guideDecal;
	private MeshInstance3D _previewPlane;
	private StandardMaterial3D _previewMat;

	private Vector3 _aimOrigin;
	private Vector3 _aimNormal;

	// 2D slice session
	private Slice2DLevel _slice2D;
	private bool _inSliceMode = false;
	private Vector3 _basisU;
	private Vector3 _basisV;
	private Vector3 _startPos3D;

	public override void _Ready()
	{
		_player = GetNodeOrNull<CharacterBody3D>(PlayerPath);
		_roomRoot = GetNodeOrNull<Node>(RoomRootPath);
		_crosshair = GetNodeOrNull<Control>(CrosshairPath);
		_cam = GetViewport()?.GetCamera3D();

		if (_cam == null)
			GD.PushWarning("[SliceManager] No active Camera3D found. Using Player basis as fallback.");

		// Auto-discover meshes if not manually assigned
		if ((TargetMeshes == null || TargetMeshes.Length == 0) && _roomRoot != null)
		{
			var found = new List<NodePath>();
			foreach (var n in _roomRoot.GetChildren())
				if (n is MeshInstance3D mi) found.Add(mi.GetPath());
			TargetMeshes = found.ToArray();
		}

		// Guide Decal
		if (UseProjectedGuide)
		{
			_guideDecal = new Decal { Visible = false };
			AddChild(_guideDecal);

			if (GuideTexture != null)
			{
				_guideDecal.TextureAlbedo = GuideTexture;
				_guideDecal.Modulate = GuideTint;
				_guideDecal.EmissionEnergy = 1.5f;
			}

			_guideDecal.UpperFade = 0f;
			_guideDecal.LowerFade = 0f;
			_guideDecal.NormalFade = 0f;
		}

		// Optional preview plane
		if (ShowPreviewPlane)
		{
			_previewPlane = new MeshInstance3D { Visible = false };
			var pm = new PlaneMesh { Size = new Vector2(2, 2) };
			_previewPlane.Mesh = pm;

			_previewMat = new StandardMaterial3D
			{
				AlbedoColor = PreviewColor,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
				Roughness = 1.0f,
				Metallic = 0.0f
			};
			if (_previewPlane.Mesh.GetSurfaceCount() > 0)
				_previewPlane.Mesh.SurfaceSetMaterial(0, _previewMat);

			AddChild(_previewPlane);
		}

		if (_crosshair != null)
			_crosshair.Visible = false;
	}

	public override void _Input(InputEvent e)
	{
		// Exit from 2D slice
		if (_inSliceMode)
		{
			if (e.IsActionPressed("slice_aim") || (e is InputEventKey k && k.Pressed && k.Keycode == Key.Escape))
				Exit2DBackTo3D();
			return;
		}

		if (e.IsActionPressed("slice_aim"))
			BeginAim();

		if (e.IsActionReleased("slice_aim"))
			EndAimAndSlice();
	}

	public override void _Process(double delta)
	{
		if ((_guideDecal != null && _guideDecal.Visible) ||
			(_previewPlane != null && _previewPlane.Visible))
			UpdatePreview();
	}

	// -----------------------------
	// Aiming
	// -----------------------------
	private void BeginAim()
	{
		if (_crosshair != null) _crosshair.Visible = true;
		if (_guideDecal != null) _guideDecal.Visible = true;
		if (_previewPlane != null) _previewPlane.Visible = ShowPreviewPlane;
		UpdatePreview();
	}

	private void EndAimAndSlice()
	{
		if (_crosshair != null) _crosshair.Visible = false;
		if (_guideDecal != null) _guideDecal.Visible = false;
		if (_previewPlane != null) _previewPlane.Visible = false;

		if (_cam != null)
		{
			_aimOrigin = _cam.GlobalPosition;
			_aimNormal = _cam.GlobalTransform.Basis.X;
		}
		else
		{
			_aimOrigin = _player.GlobalPosition;
			_aimNormal = _player.GlobalTransform.Basis.X;
		}

		if (ForceVertical) _aimNormal.Y = 0f;
		_aimNormal = _aimNormal.Normalized();

		Plane worldPlane = new Plane(_aimNormal, _aimOrigin.Dot(_aimNormal));
		Vector3 forward = (_cam != null ? -_cam.GlobalTransform.Basis.Z : -_player.GlobalTransform.Basis.Z).Normalized();
		_basisU = forward;
		_basisV = _aimNormal.Cross(_basisU).Normalized();
		if (_basisV.LengthSquared() < 1e-6f) _basisV = Vector3.Up;

		var perMeshLoops = new List<Slice2DLevel.SlicePolygon>();

		// Slice visible meshes (TargetMeshes)
		foreach (var path in TargetMeshes ?? Array.Empty<NodePath>())
		{
			var mi = GetNodeOrNull<MeshInstance3D>(path);
			if (mi == null || mi.Mesh == null) continue;

			var sourceAM = ConvertToArrayMesh(mi.Mesh);
			if (sourceAM == null) continue;

			var toLocal = mi.GlobalTransform.AffineInverse();

			Vector3 origin = _aimOrigin;
			Vector3 normal = _aimNormal;
			Vector3 tangent = BuildPlaneTangent(normal);
			Vector3 bitangent = normal.Cross(tangent);

			Vector3 wp0 = origin;
			Vector3 wp1 = origin + tangent;
			Vector3 wp2 = origin + bitangent;

			Vector3 lp0 = toLocal * wp0;
			Vector3 lp1 = toLocal * wp1;
			Vector3 lp2 = toLocal * wp2;

			Vector3 lN = (lp1 - lp0).Cross(lp2 - lp0).Normalized();
			Plane localPlane = new Plane(lN, lp0.Dot(lN));

			var res = SliceMeshUtility.Slice(sourceAM, localPlane);

			if (res.FrontMesh != null)
				mi.Mesh = res.FrontMesh;

			if (KeepBackHalf && res.BackMesh != null)
			{
				var backNode = new MeshInstance3D
				{
					Mesh = res.BackMesh,
					GlobalTransform = mi.GlobalTransform
				};
				mi.GetParent().AddChild(backNode);
			}

			if (res.SectionLoop != null && res.SectionLoop.Count >= 3)
			{
				var sortedWorld = SortLoopOnPlane(worldPlane, res.SectionLoop, mi.GlobalTransform);
				if (sortedWorld != null && sortedWorld.Count >= 3)
					perMeshLoops.Add(new Slice2DLevel.SlicePolygon { WorldLoop = sortedWorld });
			}
		}

		// Detect and project any other MeshInstance3D in RoomRoot that the plane intersects
		if (_roomRoot != null)
		{
			foreach (var n in _roomRoot.GetChildren())
			{
				var mi = n as MeshInstance3D;
				if (mi == null || mi.Mesh == null) continue;

				// If it's already in TargetMeshes, we already processed it; skipping duplicate work is optional
				if (TargetMeshes != null && Array.Exists(TargetMeshes, p => GetNodeOrNull<MeshInstance3D>(p) == mi))
					continue;

				if (AabbIntersectsPlane(mi.GetAabb(), mi.GlobalTransform, worldPlane))
				{
					var aabb = mi.GetAabb();
					var corners = new Vector3[8];
					for (int i = 0; i < 8; i++)
						corners[i] = mi.GlobalTransform * aabb.GetEndpoint(i);

					var section = SliceMeshUtility.IntersectAabbWithPlane(corners, worldPlane);
					if (section != null && section.Count >= 3)
					{
						var sortedWorld = SortLoopOnPlane(worldPlane, section, Transform3D.Identity);
						perMeshLoops.Add(new Slice2DLevel.SlicePolygon { WorldLoop = sortedWorld });
					}
				}
			}
		}

		// Enter 2D mode
		if (perMeshLoops.Count > 0)
		{
			_inSliceMode = true;
			_startPos3D = _player.GlobalPosition;
			_player.Visible = false;
			_player.SetProcess(false);
			_player.SetPhysicsProcess(false);

			_slice2D = new Slice2DLevel();
			GetTree().Root.AddChild(_slice2D);
			_slice2D.BuildAndEnter(
				GetTree().Root,
				_aimOrigin, _basisU, _basisV,
				perMeshLoops,
				GuideTint // pass tint to 2D for nicer visuals
			);
		}
	}

	private void UpdatePreview()
	{
		if (_cam == null && _player == null) return;

		Vector3 origin = _cam != null ? _cam.GlobalPosition : _player.GlobalPosition;
		Basis camBasis = _cam != null ? _cam.GlobalTransform.Basis : _player.GlobalTransform.Basis;

		_aimNormal = camBasis.X;
		if (ForceVertical) _aimNormal.Y = 0f;
		_aimNormal = _aimNormal.Normalized();

		_aimOrigin = origin;
		Vector3 forward = (-camBasis.Z).Normalized();

		var space = GetWorld3D().DirectSpaceState;
		var rayFront = new PhysicsRayQueryParameters3D
		{
			From = origin,
			To = origin + forward * MaxPreviewDistance
		};
		var rayBack = new PhysicsRayQueryParameters3D
		{
			From = origin,
			To = origin - forward * MaxPreviewDistance
		};

		var hitF = space.IntersectRay(rayFront);
		var hitB = space.IntersectRay(rayBack);

		float distFront = (hitF.Count > 0 && hitF.ContainsKey("position")) ? ((Vector3)hitF["position"] - origin).Length() : MaxPreviewDistance;
		float distBack = (hitB.Count > 0 && hitB.ContainsKey("position")) ? ((Vector3)hitB["position"] - origin).Length() : MaxPreviewDistance;

		Vector3 widthAxis = forward;
		Vector3 normal = _aimNormal;
		Vector3 heightAxis = normal.Cross(widthAxis).Normalized();
		if (heightAxis.LengthSquared() < 1e-6f) heightAxis = Vector3.Up;
		Vector3 mid = origin + forward * ((distFront - distBack) / 2f);

		if (_guideDecal != null && _guideDecal.Visible)
		{
			float width = Mathf.Max(0.1f, distFront + distBack);
			float height = Mathf.Max(0.1f, GuideHeight);
			float depth = Mathf.Max(0.02f, GuideThickness);
			_guideDecal.Size = new Vector3(width, height, depth);

			Basis decalBasis = new Basis(widthAxis, heightAxis, normal);
			_guideDecal.GlobalTransform = new Transform3D(decalBasis, mid);
		}

		if (_previewPlane != null && _previewPlane.Visible)
		{
			if (_previewPlane.Mesh is PlaneMesh pm)
				pm.Size = new Vector2(Mathf.Max(0.1f, distFront + distBack), 20.0f);
			Basis planeBasis = new Basis(widthAxis, normal, heightAxis);
			_previewPlane.GlobalTransform = new Transform3D(planeBasis, mid);
		}
	}

	private void Exit2DBackTo3D()
	{
		if (!_inSliceMode || _slice2D == null) return;

		float du = _slice2D.ExitAndGetUDisplacement();
		Vector3 delta3D = _basisU * du;

		_player.GlobalPosition = _startPos3D + delta3D;
		_player.Visible = true;
		_player.SetProcess(true);
		_player.SetPhysicsProcess(true);

		_inSliceMode = false;
		_slice2D = null;
	}

	// -----------------------------
	// Helpers
	// -----------------------------
	private static Vector3 BuildPlaneTangent(Vector3 n)
	{
		Vector3 up = MathF.Abs(n.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
		return up.Cross(n).Normalized();
	}

	private static ArrayMesh ConvertToArrayMesh(Mesh mesh)
	{
		if (mesh is ArrayMesh am) return am;
		var newAM = new ArrayMesh();
		int sc = mesh.GetSurfaceCount();
		for (int s = 0; s < sc; s++)
		{
			var arrays = mesh.SurfaceGetArrays(s);
			newAM.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		}
		return newAM;
	}

	private List<Vector3> SortLoopOnPlane(Plane plane, List<Vector3> localPts, Transform3D localToWorld)
	{
		if (localPts == null || localPts.Count < 3) return null;

		Vector3 n = plane.Normal.Normalized();
		Vector3 t = BuildPlaneTangent(n);
		Vector3 b = n.Cross(t);

		var worldPts = new List<Vector3>(localPts.Count);
		Vector3 center = Vector3.Zero;
		foreach (var lp in localPts)
		{
			var wp = localToWorld * lp;
			worldPts.Add(wp);
			center += wp;
		}
		center /= worldPts.Count;

		worldPts.Sort((p1, p2) =>
		{
			Vector3 r1 = p1 - center;
			Vector3 r2 = p2 - center;
			float a1 = Mathf.Atan2(r1.Dot(b), r1.Dot(t));
			float a2 = Mathf.Atan2(r2.Dot(b), r2.Dot(t));
			return a1.CompareTo(a2);
		});

		return worldPts;
	}

	private bool AabbIntersectsPlane(Aabb box, Transform3D xf, Plane plane)
	{
		var corners = new Vector3[8];
		for (int i = 0; i < 8; i++)
			corners[i] = xf * box.GetEndpoint(i);

		bool hasPos = false, hasNeg = false;
		foreach (var c in corners)
		{
			float d = plane.DistanceTo(c);
			if (d > 0) hasPos = true;
			if (d < 0) hasNeg = true;
		}
		return hasPos && hasNeg;
	}
}
