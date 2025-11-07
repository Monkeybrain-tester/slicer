// File: SlicerTestRoom/SliceManager.cs
using Godot;
using System;
using System.Linq;
using System.Collections.Generic;

public partial class SliceManager : Node3D
{
	// ---------- Links ----------
	[ExportGroup("Scene Links")]
	[Export] public NodePath PlayerPath;     // CharacterBody3D (your Player1)
	[Export] public NodePath RoomRootPath;   // Parent of sliceable meshes
	[Export] public NodePath[] TargetMeshes; // Optional explicit mesh list

	[ExportGroup("UI")]
	[Export] public NodePath CrosshairPath;  // Optional crosshair Control

	// ---------- Preview (Decal guide) ----------
	[ExportGroup("Projected Guide")]
	[Export] public bool UseProjectedGuide = true;
	[Export] public Texture2D GuideTexture;                      // dashed texture
	[Export] public Color GuideTint = new Color(0.9f, 0.5f, 1, 1);
	[Export] public float GuideHeight = 6.0f;                    // visual height
	[Export] public float GuideThickness = 0.25f;                // decal depth
	[Export] public float MaxPreviewDistance = 200.0f;

	// ---------- Slice behavior / visuals ----------
	[ExportGroup("Slice Behavior")]
	[Export] public bool KeepBackHalf = false;                   // keep the sliced-away side as a new MeshInstance3D
	[Export] public bool ForceVertical = true;                   // zero Y on plane normal (vertical slice)
	[Export] public bool ShowCap = true;                         // fill the cut with a cap
	[Export] public Material CapMaterial;                        // material for visual cap

	// ---------- 2D Runtime ----------
	[ExportGroup("2D Runtime")]
	[Export] public string Slice2DScenePath = "res://2Dportion/Slice2D.tscn";
	[Export] public float SliceUnitsPerPixel = 0.02f;            // 3D meters per 2D pixel

	// ---------- Input ----------
	[ExportGroup("Input Actions")]
	[Export] public string SliceAimAction = "slice_aim";         // LMB (hold)
	[Export] public string SliceExitAction = "slice_exit";       // RMB / E (in 2D)

	// ---------- Vertical Snap 3D return ----------
	[ExportGroup("Return From 2D")]
	[Export] public float ReturnUpNudge = 0.25f;   // small upward lift (meters)
	[Export] public float GroundSnapProbe = 2.0f; // how far down we raycast to find ground
	[Export] public float StandClearance = 0.05f; // gap above the ground after snap
	[Export] public bool  EnableGroundSnap = true;

[ExportGroup("Return Placement")]
[Export] public float GroundRayUp = 0.6f;      // ray starts this far above target
[Export] public float GroundRayDown = 2.0f;    // and casts this far below
[Export] public float DeadzonePixels = 6f;     // suppress tiny 2D jitters on exit


	// ---------- Private state ----------
	private CharacterBody3D _player;
	private Node _roomRoot;
	private Control _crosshair;
	private Camera3D _cam;

	private Decal _guideDecal;

	// current aim plane (world)
	private Vector3 _aimOrigin;
	private Vector3 _aimNormal; // camera RIGHT (vertical plane)
	private Vector3 _slicePlaneNormal = Vector3.Forward; // default until slice created



	// plane basis used to project 3D → 2D (U forward, V up-in-plane)
	private Vector3 _basisU;      // forward along camera view (−Z)
	private Vector3 _basisV;      // n × U  (up inside the plane)

	// ------------------------------------------------------------
	public override void _Ready()
	{
		_player = GetNodeOrNull<CharacterBody3D>(PlayerPath);
		_roomRoot = GetNodeOrNull<Node>(RoomRootPath);
		_crosshair = GetNodeOrNull<Control>(CrosshairPath);
		_cam = GetViewport()?.GetCamera3D();

		EnsureInputActions();

		// Auto-discover meshes if not provided
if ((TargetMeshes == null || TargetMeshes.Length == 0))
	AutoDiscoverSliceTargetsFromGroup("sliceable");


		// Build preview decal
		if (UseProjectedGuide)
		{
			_guideDecal = new Decal { Visible = false };
			AddChild(_guideDecal);

			if (GuideTexture != null)
			{
				_guideDecal.TextureAlbedo = GuideTexture;
				_guideDecal.Modulate = GuideTint;
				_guideDecal.EmissionEnergy = 1.15f;
			}
			_guideDecal.UpperFade = 0f;
			_guideDecal.LowerFade = 0f;
			_guideDecal.NormalFade = 0f;
		}

		if (_crosshair != null) _crosshair.Visible = false;
	}

	public override void _Input(InputEvent e)
	{
		if (e.IsActionPressed(SliceAimAction))
			BeginAim();
		if (e.IsActionReleased(SliceAimAction))
			EndAimAndSlice();
	}

	public override void _Process(double delta)
	{
		if (_guideDecal != null && _guideDecal.Visible)
			UpdatePreview();
	}

	// ------------------------------------------------------------
	// Aim / Preview
	// ------------------------------------------------------------
	private void BeginAim()
	{
		if (_crosshair != null) _crosshair.Visible = true;
		if (_guideDecal != null) _guideDecal.Visible = true;
		UpdatePreview();
	}

private void EndAimAndSlice()
{
	if (_crosshair != null) _crosshair.Visible = false;
	if (_guideDecal != null) _guideDecal.Visible = false;

	// --- Step 1: Capture aim origin & normal from camera or player ---
	if (_cam != null)
	{
		_aimOrigin = _cam.GlobalPosition;
		_aimNormal = _cam.GlobalTransform.Basis.X;   // camera right vector
	}
	else if (_player != null)
	{
		_aimOrigin = _player.GlobalPosition;
		_aimNormal = _player.GlobalTransform.Basis.X;
	}
	else
	{
		_aimOrigin = Vector3.Zero;
		_aimNormal = Vector3.Right;
	}

	// --- Step 2: Force vertical slice (remove camera pitch) ---
	if (ForceVertical)
	{
		_aimNormal.Y = 0f;                      // flatten tilt
		if (_aimNormal.LengthSquared() < 1e-6f)
			_aimNormal = Vector3.Right;         // fallback
	}
	_aimNormal = _aimNormal.Normalized();

	// --- Step 3: Define 2D-projection basis ---
	// 2D.X = horizontal along slice plane
	// 2D.Y = world up
	_basisV = Vector3.Up;                                       // up/down in slice = world Y
	_basisU = _basisV.Cross(_aimNormal).Normalized();           // horizontal axis in slice
	if (_basisU.LengthSquared() < 1e-6f) _basisU = Vector3.Forward;
	_basisV = _basisV.Normalized();

	// --- Step 4: Record slice normal for return translation ---
	_slicePlaneNormal = _aimNormal;                             // store for OnSliceExit2D()

	// --- Optional: debug print ---
	GD.Print($"[SliceManager] Slice Plane -> Normal:{_slicePlaneNormal}  U:{_basisU}  V:{_basisV}");

	// --- Step 5: Execute the slice ---
	ExecuteSlice();
}


	private void UpdatePreview()
	{
		if (_cam == null && _player == null) return;

		var origin = _cam != null ? _cam.GlobalPosition : _player.GlobalPosition;
		var cb = _cam != null ? _cam.GlobalTransform.Basis : _player.GlobalTransform.Basis;

		// plane normal = camera RIGHT
		_aimNormal = cb.X;
		if (ForceVertical) _aimNormal.Y = 0f;
		_aimNormal = _aimNormal.Normalized();

		// width axis = camera FORWARD (so dashes go away from you)
		Vector3 widthAxis = (-cb.Z).Normalized();
		Vector3 heightAxis = _aimNormal.Cross(widthAxis).Normalized();
		if (heightAxis.LengthSquared() < 1e-6f) heightAxis = Vector3.Up;

		// bound the decal by raycasting forward/back
		var space = GetWorld3D().DirectSpaceState;
		var front = PhysicsRayQueryParameters3D.Create(origin, origin + widthAxis * MaxPreviewDistance);
		front.CollideWithBodies = true; front.CollideWithAreas = false;

		var back  = PhysicsRayQueryParameters3D.Create(origin, origin - widthAxis * MaxPreviewDistance);
		back.CollideWithBodies = true; back.CollideWithAreas = false;

		var hitF = space.IntersectRay(front);
		var hitB = space.IntersectRay(back);

		float dF = (hitF.Count > 0 && hitF.ContainsKey("position")) ? ((Vector3)hitF["position"] - origin).Length() : MaxPreviewDistance;
		float dB = (hitB.Count > 0 && hitB.ContainsKey("position")) ? ((Vector3)hitB["position"] - origin).Length() : MaxPreviewDistance;

		Vector3 center = origin + widthAxis * ((dF - dB) * 0.5f);

		if (_guideDecal != null)
		{
			_guideDecal.Size = new Vector3(Mathf.Max(0.1f, dF + dB), Mathf.Max(0.1f, GuideHeight), Mathf.Max(0.02f, GuideThickness));
			Basis decalBasis = new Basis(widthAxis, heightAxis, _aimNormal); // +Z = normal
			_guideDecal.GlobalTransform = new Transform3D(decalBasis, center);
		}
	}

	// ------------------------------------------------------------
	// Slice execution
	// ------------------------------------------------------------
	private void ExecuteSlice()
	{
		var worldPlane = new Plane(_aimNormal, _aimOrigin.Dot(_aimNormal));
		_slicePlaneNormal = _aimNormal.Normalized();

		// For 2D world
		var polygons2D = new List<List<Vector2>>();
		var segments2D = new List<Vector2[]>();

		// For 3D cap (optional)
		var capPointsWorld = new List<Vector3>();

		// Prepare 3 distinct world points that lie on the plane to transform into local space
		Vector3 t = BuildPlaneTangent(_aimNormal);
		Vector3 b = _aimNormal.Cross(t);
		Vector3 wp0 = _aimOrigin;
		Vector3 wp1 = _aimOrigin + t;
		Vector3 wp2 = _aimOrigin + b;

		foreach (var path in TargetMeshes ?? Array.Empty<NodePath>())
		{
			var mi = GetNodeOrNull<MeshInstance3D>(path);
			if (mi == null || mi.Mesh == null) continue;

			var am = ConvertToArrayMesh(mi.Mesh);
			if (am == null) continue;

			// plane → mesh local (3-point method)
			var toLocal = mi.GlobalTransform.AffineInverse();
			Vector3 lp0 = toLocal * wp0;
			Vector3 lp1 = toLocal * wp1;
			Vector3 lp2 = toLocal * wp2;
			Vector3 lN  = (lp1 - lp0).Cross(lp2 - lp0).Normalized();
			Plane localPlane = new Plane(lN, lp0.Dot(lN));

			// slice
			var res = SliceMeshUtility.Slice(am, localPlane);

			// keep front mesh in the original node
			if (res.FrontMesh != null)
				mi.Mesh = res.FrontMesh;

			// optionally keep back half as a sibling
			if (KeepBackHalf && res.BackMesh != null)
			{
				var back = new MeshInstance3D
				{
					Mesh = res.BackMesh,
					GlobalTransform = mi.GlobalTransform
				};
				mi.GetParent().AddChild(back);
			}

			// collect cross-section points (local→world)
			if (res.SectionLoop != null && res.SectionLoop.Count > 0)
			{
				var loopWorld = new List<Vector3>(res.SectionLoop.Count);
				foreach (var lp in res.SectionLoop) loopWorld.Add(mi.GlobalTransform * lp);

				// add to 3D cap pool
				capPointsWorld.AddRange(loopWorld);

				// project to 2D slice space (U,V)
				var loop2D = new List<Vector2>(loopWorld.Count);
				foreach (var wp in loopWorld)
				{
					Vector3 r = wp - _aimOrigin;
					float u = r.Dot(_basisU);
					float v = r.Dot(_basisV);
					loop2D.Add(new Vector2(u / SliceUnitsPerPixel, -v / SliceUnitsPerPixel)); // flip V for 2D up
				}

				// sort CCW around centroid
				polygons2D.Add(SortLoopCCW(loop2D));
			}

			// if the mesh is too thin and only intersects as a line, SliceMeshUtility
			// can expose SectionSegments (Vector3[2]) — if you added that, project them:
			if (res.SectionSegments != null)
			{
				foreach (var seg in res.SectionSegments)
				{
					Vector2 a = ProjectWorldTo2D(mi.GlobalTransform * seg[0]);
					Vector2 c = ProjectWorldTo2D(mi.GlobalTransform * seg[1]);
					segments2D.Add(new[] { a, c });
				}
			}
		}

		// optional visual cap in 3D
		if (ShowCap && capPointsWorld.Count >= 3)
		{
			var cap = BuildCap(worldPlane, capPointsWorld, CapMaterial);
			if (cap != null) AddChild(cap);
		}

		// hand off to 2D
		LaunchSlice2D(polygons2D, segments2D);
	}

	// ------------------------------------------------------------
	// 2D scene launch / return
	// ------------------------------------------------------------
	private void LaunchSlice2D(List<List<Vector2>> polygons2D, List<Vector2[]> segments2D)
	{
		var scene = GD.Load<PackedScene>(Slice2DScenePath);
		if (scene == null)
		{
			GD.PushError($"[SliceManager] Cannot load 2D scene: {Slice2DScenePath}");
			return;
		}

		var slice = scene.Instantiate<Node2D>() as Node2D;
		if (slice is not SliceLevel2D level)
		{
			GD.PushError("Slice2D.tscn root must have SliceLevel2D.cs attached.");
			return;
		}
		
		 // turn off 3D camera while in 2D
		if (_cam != null) _cam.Current = false;  


		// pack data
		level.Polygons2D = polygons2D ?? new();
		level.Segments2D = segments2D ?? new();

		// player start in 2D = projection of current 3D player onto plane
		Vector3 projected = ProjectPointToPlane(_player.GlobalPosition, new Plane(_aimNormal, _aimOrigin.Dot(_aimNormal)));
		Vector2 start2D   = ProjectWorldTo2D(projected);
		level.PlayerStart2D = start2D;

		// add + connect
		GetTree().CurrentScene.AddChild(level);
		if (_player != null) _player.Visible = false;

		level.Connect(SliceLevel2D.SignalName.SliceExit, new Callable(this, nameof(OnSliceExit2D)));
	}
	
private void OnSliceExit2D(Vector2 delta2D)
{
	// 1) Deadzone tiny jitters from 2D frame (e.g., 1–6 px)
	if (Mathf.Abs(delta2D.X) < DeadzonePixels) delta2D.X = 0f;
	if (Mathf.Abs(delta2D.Y) < DeadzonePixels) delta2D.Y = 0f;

	// 2) Map 2D Δ back to 3D using the SAME basis we used to project into 2D.
	//    Remember: we flipped V when projecting (v -> -Y), so undo that here with a minus.
	Vector3 delta3D =
		_basisU * (delta2D.X * SliceUnitsPerPixel) +
		_basisV * (-delta2D.Y * SliceUnitsPerPixel);

	// 3) Compute the *target* world position on the slice plane + delta
	Plane plane = new Plane(_aimNormal, _aimOrigin.Dot(_aimNormal));
	Vector3 startOnPlane = ProjectPointToPlane(_player.GlobalPosition, plane);
	Vector3 desired = startOnPlane + delta3D;

	// 4) Place safely on ground at desired (raycast down from a bit above),
	//    then add a tiny upward nudge to prevent clipping.
	Vector3 safe = SnapToGround(desired, GroundRayUp, GroundRayDown, ReturnUpNudge);

	// 5) Hard-reset motion so gravity doesn’t tick before snap locks in.
	_player.Velocity = Vector3.Zero;
	_player.FloorSnapLength = 0.0f;  // disable auto-snap for the first frame

	_player.GlobalPosition = safe;

	// Optional: one-frame skip flag (set on player via metadata)
	_player.SetMeta("justReturnedFromSlice", true);

	// Restore 3D view
	if (_cam != null) _cam.Current = true;
	_player.Visible = true;

	GD.Print($"[SliceManager] Returned to 3D. Δ3D={delta3D}, snapped={safe}");
}









	// ------------------------------------------------------------
	// Helpers
	// ------------------------------------------------------------
	private void EnsureInputActions()
	{
		if (!InputMap.HasAction(SliceAimAction))
		{
			InputMap.AddAction(SliceAimAction);
			InputMap.ActionAddEvent(SliceAimAction, new InputEventMouseButton { ButtonIndex = MouseButton.Left });
		}
		if (!InputMap.HasAction(SliceExitAction))
		{
			InputMap.AddAction(SliceExitAction);
			InputMap.ActionAddEvent(SliceExitAction, new InputEventMouseButton { ButtonIndex = MouseButton.Right });
			InputMap.ActionAddEvent(SliceExitAction, new InputEventKey { Keycode = Key.E });
			InputMap.ActionAddEvent(SliceExitAction, new InputEventKey { Keycode = Key.Escape });
		}
	}

	private static Vector3 BuildPlaneTangent(Vector3 n)
	{
		// pick an "up-ish" that isn't colinear
		Vector3 up = MathF.Abs(n.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
		return up.Cross(n).Normalized();
	}

	private static ArrayMesh ConvertToArrayMesh(Mesh mesh)
	{
		if (mesh is ArrayMesh am) return am;
		var newAM = new ArrayMesh();
		int sc = mesh.GetSurfaceCount();
		for (int s = 0; s < sc; s++)
			newAM.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, mesh.SurfaceGetArrays(s));
		return newAM;
	}

	private MeshInstance3D BuildCap(Plane plane, List<Vector3> pointsWorld, Material mat)
	{
		if (pointsWorld == null || pointsWorld.Count < 3) return null;

		// sort CCW in plane space around centroid
		Vector3 n = plane.Normal.Normalized();
		Vector3 t = BuildPlaneTangent(n);
		Vector3 b = n.Cross(t);
		Vector3 c = Vector3.Zero; foreach (var p in pointsWorld) c += p; c /= pointsWorld.Count;

		var sorted = pointsWorld
			.Select(p => {
				Vector3 r = p - c;
				float u = r.Dot(t);
				float v = r.Dot(b);
				float a = Mathf.Atan2(v, u);
				return (p, a);
			})
			.OrderBy(x => x.a)
			.Select(x => x.p)
			.ToList();

		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);

		for (int i = 0; i < sorted.Count; i++)
		{
			Vector3 a = sorted[i];
			Vector3 c2 = sorted[(i + 1) % sorted.Count];
			st.SetNormal(n); st.AddVertex(c);
			st.SetNormal(n); st.AddVertex(a);
			st.SetNormal(n); st.AddVertex(c2);
		}

		var cap = st.Commit();
		if (cap == null) return null;
		if (mat != null && cap.GetSurfaceCount() > 0) cap.SurfaceSetMaterial(0, mat);

		return new MeshInstance3D { Mesh = cap };
	}

	private static Vector3 ProjectPointToPlane(Vector3 p, Plane plane)
	{
		float d = plane.DistanceTo(p);
		return p - plane.Normal * d;
	}

	private Vector2 ProjectWorldTo2D(Vector3 wp)
	{
		Vector3 r = wp - _aimOrigin;
		float u = r.Dot(_basisU);
		float v = r.Dot(_basisV);
		return new Vector2(u / SliceUnitsPerPixel, -v / SliceUnitsPerPixel);
	}

	private static List<Vector2> SortLoopCCW(List<Vector2> pts)
	{
		if (pts == null || pts.Count < 3) return pts ?? new();
		Vector2 c = Vector2.Zero; foreach (var p in pts) c += p; c /= pts.Count;
		pts.Sort((a, b) =>
		{
			float aa = Mathf.Atan2(a.Y - c.Y, a.X - c.X);
			float bb = Mathf.Atan2(b.Y - c.Y, b.X - c.X);
			return aa.CompareTo(bb);
		});
		return pts;
	}
	
	private void AutoDiscoverSliceTargetsFromGroup(string group = "sliceable") {
	var list = new System.Collections.Generic.List<NodePath>();
	foreach (var n in GetTree().GetNodesInGroup(group)) {
		if (n is MeshInstance3D mi && mi.Mesh != null)
			list.Add(mi.GetPath());
	}
	if (list.Count > 0) TargetMeshes = list.ToArray();
}

private Vector3 FindSafeLanding(Vector3 target, float upNudge = 0.3f, float standClearance = 0.5f, float groundProbe = 3.0f)
{
	// 1) Nudge up a bit to avoid embedding in sloped/nearby geometry
	Vector3 candidate = target + Vector3.Up * upNudge;

	// 2) Raycast down to find the floor and standClearance above it
	var space = GetWorld3D().DirectSpaceState;
	var from  = candidate + Vector3.Up * groundProbe;
	var to    = candidate + Vector3.Down * (groundProbe * 4f);

	var q = PhysicsRayQueryParameters3D.Create(from, to);
	q.CollideWithAreas = false;
	q.CollideWithBodies = true;

	var hit = space.IntersectRay(q);
	if (hit.Count > 0 && hit.ContainsKey("position"))
	{
		Vector3 floor = (Vector3)hit["position"];
		return floor + Vector3.Up * standClearance;
	}

	// If no floor found, just return the up-nudged target
	return candidate;
}

private Vector3 SnapToGround(Vector3 target, float up, float down, float upNudge)
{
	var space = GetWorld3D().DirectSpaceState;

	// Downward ray (floor check)
	Vector3 fromDown = target + Vector3.Up * 0.1f; // start slightly above target
	Vector3 toDown   = target - Vector3.Up * down;
	var qDown = PhysicsRayQueryParameters3D.Create(fromDown, toDown);
	qDown.CollideWithAreas = false;
	qDown.CollideWithBodies = true;
	var hitDown = space.IntersectRay(qDown);

	// Upward ray (roof check)
	Vector3 fromUp = target - Vector3.Up * 0.1f;
	Vector3 toUp   = target + Vector3.Up * up;
	var qUp = PhysicsRayQueryParameters3D.Create(fromUp, toUp);
	qUp.CollideWithAreas = false;
	qUp.CollideWithBodies = true;
	var hitUp = space.IntersectRay(qUp);



	if (hitUp.Count > 0 && hitUp.ContainsKey("position"))
	{
		Vector3 roofPos = (Vector3)hitUp["position"];
		// Move slightly below ceiling to avoid embedding
		return roofPos - Vector3.Up * (upNudge * 2f);
	}

	// If we find a floor below the target -> use it


	// If we're trapped in or below a ceiling (hit above, none below)

	
	GD.Print($"SnapToGround: targetY={target.Y} floorHit={(hitDown.Count>0)} roofHit={(hitUp.Count>0)}");


	// No hits at all, keep position + gentle up nudge
	return target + Vector3.Up * upNudge;
}



// Guess a reasonable half-height if you don't have it handy
private float GetPlayerHalfHeight()
{
	// If you have a CollisionShape3D on the player, read its shape to be exact.
	// Fallback to ~0.9m (a ~1.8m tall capsule/box).
	return 0.9f;
}


}
