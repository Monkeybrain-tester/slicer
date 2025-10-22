// File: SliceManager.cs
using Godot;
using System;
using System.Linq;
using System.Collections.Generic;


/// Hold LMB (slice_aim) to preview a slice as a projected dashed line (Decal).
/// Release LMB to execute a true infinite plane slice across target meshes,
/// using SliceMeshUtility.Slice(...). Optional visual cap is generated.
/// After slicing, this launches a separate 2D scene (Slice2D.tscn) built from the cross-sections.
/// When the 2D scene emits SliceExit(delta2D), we translate the 3D player along the slice plane.

public partial class SliceManager : Node3D
{
	// -----------------------------
	// Scene links / targets
	// -----------------------------
	[ExportGroup("Scene Links")]
	[Export] public NodePath PlayerPath;            // Your Player1 (optional if using camera only)
	[Export] public NodePath RoomRootPath;          // Parent containing meshes to slice (optional)
	[Export] public NodePath[] TargetMeshes;        // Explicit MeshInstance3D list (optional)

	[ExportGroup("UI")]
	[Export] public NodePath CrosshairPath;         // Control with your crosshair script (optional)

	// -----------------------------
	// Projected dashed guide (Decal)
	// -----------------------------
	[ExportGroup("Projected Guide")]
	[Export] public bool UseProjectedGuide = true;
	[Export] public Texture2D GuideTexture;         // e.g., res://art/dashed_purple_tiled.png
	[Export] public Color GuideTint = new Color(0.9f, 0.5f, 1.0f, 1.0f);
	[Export] public float GuideHeight = 6.0f;       // local Y size of the decal box
	[Export] public float GuideThickness = 0.25f;   // local Z size (projection depth)
	[Export] public float MaxPreviewDistance = 200.0f;

	// -----------------------------
	// Optional translucent plane (off by default in FPS)
	// -----------------------------
	[ExportGroup("Legacy Plane Preview")]
	[Export] public bool ShowPreviewPlane = false;  // keep false; decal is better in FPS
	[Export] public Color PreviewColor = new Color(0.9f, 0.2f, 0.2f, 0.25f);

	// -----------------------------
	// Slice visuals & behavior
	// -----------------------------
	[ExportGroup("Slice Visuals")]
	[Export] public bool ShowCap = true;
	[Export] public Material CapMaterial;

	[ExportGroup("Slice Behavior")]
	[Export] public bool KeepBackHalf = false;      // Keep back mesh as a sibling copy
	[Export] public bool ForceVertical = true;      // Zero Y on plane normal for vertical slices

	// -----------------------------
	// Private state
	// -----------------------------
	private CharacterBody3D _player;
	private Node _roomRoot;
	private Control _crosshair;
	private Camera3D _cam;

	// Decal guide (projected dashed line)
	private Decal _guideDecal;

	// Optional plane preview
	private MeshInstance3D _previewPlane;
	private StandardMaterial3D _previewMat;

	// Aim plane (world space)
	private Vector3 _aimOrigin;   // world
	private Vector3 _aimNormal;   // world (plane normal = camera RIGHT)

	// -----------------------------
	// Lifecycle
	// -----------------------------
	public override void _Ready()
	{
		_player = GetNodeOrNull<CharacterBody3D>(PlayerPath);
		_roomRoot = GetNodeOrNull<Node>(RoomRootPath);
		_crosshair = GetNodeOrNull<Control>(CrosshairPath);
		_cam = GetViewport()?.GetCamera3D();

		if (_cam == null)
			GD.PushWarning("[SliceManager] No active Camera3D found. Crosshair alignment may be off.");

		// If no explicit TargetMeshes, auto-discover MeshInstance3D under RoomRoot
		if ((TargetMeshes == null || TargetMeshes.Length == 0) && _roomRoot != null)
		{
			var found = new List<NodePath>();
			foreach (var n in _roomRoot.GetChildren())
				if (n is MeshInstance3D mi) found.Add(mi.GetPath());
			TargetMeshes = found.ToArray();
		}

		// Build projected dashed guide decal
		if (UseProjectedGuide)
		{
			_guideDecal = new Decal { Visible = false };
			AddChild(_guideDecal);

			if (GuideTexture != null)
			{
				_guideDecal.TextureAlbedo = GuideTexture; // Decal uses its own texture slots
				_guideDecal.Modulate = GuideTint;
				_guideDecal.EmissionEnergy = 1.5f;        // small boost so it reads in dark
			}
			_guideDecal.UpperFade = 0f;
			_guideDecal.LowerFade = 0f;
			_guideDecal.NormalFade = 0f;
		}

		// Optional translucent plane (usually off)
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

		if (_crosshair != null) _crosshair.Visible = false;
	}

	public override void _Input(InputEvent e)
	{
		if (e.IsActionPressed("slice_aim"))
			BeginAim();

		if (e.IsActionReleased("slice_aim"))
			EndAimAndSlice();
	}

	public override void _Process(double delta)
	{
		// While aiming, keep the preview updated to camera/collisions
		if ((_guideDecal != null && _guideDecal.Visible) ||
			(_previewPlane != null && _previewPlane.Visible))
		{
			UpdatePreview();
		}
	}

	// -----------------------------
	// Aim / Preview
	// -----------------------------
	private void BeginAim()
	{
		if (_crosshair != null) _crosshair.Visible = true;
		if (_guideDecal != null) _guideDecal.Visible = true;
		if (_previewPlane != null) _previewPlane.Visible = ShowPreviewPlane;

		UpdatePreview(); // place immediately
	}

	private void EndAimAndSlice()
	{
		if (_crosshair != null) _crosshair.Visible = false;
		if (_guideDecal != null) _guideDecal.Visible = false;
		if (_previewPlane != null) _previewPlane.Visible = false;

		// Refresh aim from camera so execution matches preview
		if (_cam != null)
		{
			_aimOrigin = _cam.GlobalPosition;
			_aimNormal = _cam.GlobalTransform.Basis.X; // RIGHT → plane normal
		}
		else if (_player != null)
		{
			_aimOrigin = _player.GlobalPosition;
			_aimNormal = _player.GlobalTransform.Basis.X;
		}

		if (ForceVertical) _aimNormal.Y = 0f;
		_aimNormal = _aimNormal.Normalized();

		Plane worldPlane = new Plane(_aimNormal, _aimOrigin.Dot(_aimNormal));

		// Collect all section points for 2D build (and optional cap)
		var allLoops2D = new List<List<Vector2>>();           // polygons to send to 2D scene
		var allSectionPointsWorld = new List<Vector3>();      // for optional 3D cap (debug/feedback)

		// Plane basis for 2D projection
		Vector3 axisU = (-(_cam != null ? _cam.GlobalTransform.Basis.Z : _player.GlobalTransform.Basis.Z)).Normalized(); // forward
		Vector3 axisV = _aimNormal.Cross(axisU).Normalized(); // in-plane up

		foreach (var path in TargetMeshes ?? Array.Empty<NodePath>())
		{
			var mi = GetNodeOrNull<MeshInstance3D>(path);
			if (mi == null || mi.Mesh == null) continue;

			var sourceAM = ConvertToArrayMesh(mi.Mesh);
			if (sourceAM == null) continue;

			// Convert world plane to this mesh's local space (3-point method)
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

			if (res.SectionLoop != null && res.SectionLoop.Count > 0)
			{
				// Transform this mesh's loop to world and then to 2D; keep each mesh loop separate
				var loopWorld = new List<Vector3>(res.SectionLoop.Count);
				foreach (var lp in res.SectionLoop)
				{
					var wp = mi.GlobalTransform * lp;
					loopWorld.Add(wp);
					allSectionPointsWorld.Add(wp);
				}

				var loop2D = new List<Vector2>(loopWorld.Count);
				foreach (var wp in loopWorld)
					loop2D.Add(ProjectWorldPointTo2D(wp, _aimOrigin, axisU, axisV));

				allLoops2D.Add(SortLoopCCW(loop2D));
			}
		}

		// Optional visual 3D cap (debug)
		if (ShowCap && allSectionPointsWorld.Count >= 3)
		{
			var capMesh = BuildSortedCap(worldPlane, allSectionPointsWorld);
			if (capMesh != null)
			{
				var capMI = new MeshInstance3D { Mesh = capMesh };
				if (CapMaterial != null && capMesh.GetSurfaceCount() > 0)
					capMesh.SurfaceSetMaterial(0, CapMaterial);
				AddChild(capMI);
			}
		}

		// Launch the separate 2D scene with polygons built from the slice
		if (allLoops2D.Count > 0)
			LaunchSlice2D(allLoops2D);
		else
			GD.Print("No cross-section polygons found; nothing to play in 2D.");
	}

	private void UpdatePreview()
	{
		if (_cam == null && _player == null) return;

		// Drive from camera (crosshair center)
		Vector3 origin = _cam != null ? _cam.GlobalPosition : _player.GlobalPosition;
		Basis camBasis = _cam != null ? _cam.GlobalTransform.Basis : _player.GlobalTransform.Basis;

		// Plane normal is camera RIGHT → plane extends outward through view
		_aimNormal = camBasis.X;
		if (ForceVertical) _aimNormal.Y = 0f;
		_aimNormal = _aimNormal.Normalized();

		_aimOrigin = origin;
		Vector3 forward = (-camBasis.Z).Normalized();

		// Raycast forward/back to bound the *visual* guide only
		var space = GetWorld3D().DirectSpaceState;

		var rayFront = new PhysicsRayQueryParameters3D
		{
			From = origin,
			To = origin + forward * MaxPreviewDistance,
			CollideWithAreas = false,
			CollideWithBodies = true
		};
		var rayBack = new PhysicsRayQueryParameters3D
		{
			From = origin,
			To = origin - forward * MaxPreviewDistance,
			CollideWithAreas = false,
			CollideWithBodies = true
		};

		var hitF = space.IntersectRay(rayFront);
		var hitB = space.IntersectRay(rayBack);

		float distFront = (hitF.Count > 0 && hitF.ContainsKey("position")) ? ((Vector3)hitF["position"] - origin).Length() : MaxPreviewDistance;
		float distBack  = (hitB.Count > 0 && hitB.ContainsKey("position")) ? ((Vector3)hitB["position"] - origin).Length() : MaxPreviewDistance;

		// Basis for guide/plane:
		// widthAxis (local +X) = forward (so texture stretches along view)
		// normal    (local +Z for Decal projection) = _aimNormal
		// heightAxis(local +Y) = normal x width
		Vector3 widthAxis = forward;
		Vector3 normal = _aimNormal;
		Vector3 heightAxis = normal.Cross(widthAxis).Normalized();
		if (heightAxis.LengthSquared() < 1e-6f) heightAxis = Vector3.Up;

		// Midpoint between hits along forward
		Vector3 mid = origin + forward * ((distFront - distBack) / 2f);

		// ----- Update DECAL (projected dashed line) -----
		if (_guideDecal != null && _guideDecal.Visible)
		{
			float width = Mathf.Max(0.1f, distFront + distBack); // local X
			float height = Mathf.Max(0.1f, GuideHeight);         // local Y
			float depth = Mathf.Max(0.02f, GuideThickness);      // local Z (projection depth)

			_guideDecal.Size = new Vector3(width, height, depth);

			// Decal projects along -local Z → put plane normal into +Z
			Basis decalBasis = new Basis(widthAxis, heightAxis, normal);
			_guideDecal.GlobalTransform = new Transform3D(decalBasis, mid);
		}

		// ----- Update optional translucent plane (hidden by default) -----
		if (_previewPlane != null && _previewPlane.Visible)
		{
			// PlaneMesh lies in local XZ with normal +Y
			if (_previewPlane.Mesh is PlaneMesh pm)
				pm.Size = new Vector2(Mathf.Max(0.1f, distFront + distBack), 20.0f);

			// Build basis: X=forward(width), Y=normal(plane normal), Z=heightAxis
			Basis planeBasis = new Basis(widthAxis, normal, heightAxis);
			_previewPlane.GlobalTransform = new Transform3D(planeBasis, mid);
		}
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

	private ArrayMesh BuildSortedCap(Plane plane, List<Vector3> rawWorldPoints)
	{
		if (rawWorldPoints == null || rawWorldPoints.Count < 3) return null;

		Vector3 n = plane.Normal.Normalized();
		Vector3 t = BuildPlaneTangent(n);
		Vector3 b = n.Cross(t);

		Vector3 center = Vector3.Zero;
		foreach (var p in rawWorldPoints) center += p;
		center /= rawWorldPoints.Count;

		var pts = rawWorldPoints.Select(p =>
		{
			Vector3 r = p - center;
			float u = r.Dot(t);
			float v = r.Dot(b);
			float ang = Mathf.Atan2(v, u);
			return (p, ang);
		}).OrderBy(x => x.ang).Select(x => x.p).ToList();

		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);

		for (int i = 0; i < pts.Count; i++)
		{
			Vector3 a = pts[i];
			Vector3 c = pts[(i + 1) % pts.Count];

			st.SetNormal(n); st.AddVertex(center);
			st.SetNormal(n); st.AddVertex(a);
			st.SetNormal(n); st.AddVertex(c);
		}

		var cap = st.Commit();
		if (CapMaterial != null && cap.GetSurfaceCount() > 0)
			cap.SurfaceSetMaterial(0, CapMaterial);
		return cap;
	}

	// Project a world-space point onto the slice plane basis → 2D (u along forward, v along in-plane up)
	private Vector2 ProjectWorldPointTo2D(Vector3 worldPoint, Vector3 origin, Vector3 axisU, Vector3 axisV)
	{
		Vector3 r = worldPoint - origin;
		float u = r.Dot(axisU);
		float v = r.Dot(axisV);
		return new Vector2(u, v);
	}

	// Sort a polygon's points CCW in the (u,v) plane around its centroid
	private List<Vector2> SortLoopCCW(List<Vector2> pts)
	{
		if (pts.Count < 3) return pts;
		Vector2 c = Vector2.Zero;
		foreach (var p in pts) c += p;
		c /= pts.Count;

		pts.Sort((a, b) =>
		{
			float angA = Mathf.Atan2(a.Y - c.Y, a.X - c.X);
			float angB = Mathf.Atan2(b.Y - c.Y, b.X - c.X);
			return angA.CompareTo(angB);
		});
		return pts;
	}

	private void LaunchSlice2D(List<List<Vector2>> polygons2D)
	{
		// Load the 2D scene
		var sliceScene = GD.Load<PackedScene>("res://2Dportion/Slice2D.tscn");
		var slice = sliceScene.Instantiate<Node2D>() as Node2D;

		// Validate type
		var level = slice as SliceLevel2D;
		if (level == null)
		{
			GD.PushError("Slice2D.tscn root must have SliceLevel2D.cs attached.");
			return;
		}

		level.Polygons2D = polygons2D;
		level.PlayerStart2D = Vector2.Zero; // start at plane origin (can customize)

		// Add to scene tree above 3D
		GetTree().CurrentScene.AddChild(slice);

		// Hide 3D player while in 2D (optional)
		if (_player != null) _player.Visible = false;

		// When exiting 2D, translate the 3D player and re-enable 3D
		level.Connect(SliceLevel2D.SignalName.SliceExit, new Callable(this, nameof(OnSliceExit2D)));
	}

	private void OnSliceExit2D(Vector2 delta2D)
	{
		// Convert 2D displacement back to 3D along plane: Δ3D = U*dx + V*dy
		Vector3 axisU = (-(_cam != null ? _cam.GlobalTransform.Basis.Z : _player.GlobalTransform.Basis.Z)).Normalized(); // forward
		Vector3 axisV = _aimNormal.Cross(axisU).Normalized();

		Vector3 delta3D = axisU * delta2D.X + axisV * delta2D.Y;

		if (_player != null)
		{
			_player.GlobalPosition = _aimOrigin + delta3D;
			_player.Visible = true;
		}
	}
}
