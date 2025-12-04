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

	// Hard-lock pitch to horizon while aiming
	if (_player is Player1 p1) p1.LockPitchToHorizon();

	UpdatePreview();
}


private void EndAimAndSlice()
{
	if (_crosshair != null) _crosshair.Visible = false;
	if (_guideDecal != null) _guideDecal.Visible = false;

	// Unlock vertical look now that we captured the slice plane
	if (_player is Player1 p1) p1.UnlockPitch();

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
	// Plane in world, plus the 2D axes we’ll use
	var worldPlane = new Plane(_aimNormal, _aimOrigin.Dot(_aimNormal));

	// Axis convention: U = forward (−camera.Z), V = in-plane up (n × U).
	// We already computed these in EndAimAndSlice();
	// They define the 3D→2D projection used below.

	var segmentsWorld = new List<(Vector3 A, Vector3 B)>();
	var capPointsWorld = new List<Vector3>(); // still useful for a visual cap

	// Precompute 3 points on the plane and transform per mesh → local plane
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

		// world plane → local plane via 3-point method
		var toLocal = mi.GlobalTransform.AffineInverse();
		Vector3 lp0 = toLocal * wp0;
		Vector3 lp1 = toLocal * wp1;
		Vector3 lp2 = toLocal * wp2;
		Vector3 lN  = (lp1 - lp0).Cross(lp2 - lp0).Normalized();
		Plane localPlane = new Plane(lN, lp0.Dot(lN));

		// slice: require SectionSegments from SliceMeshUtility
		var res = SliceMeshUtility.Slice(am, localPlane);

		// (optional) keep front/back meshes as you already do
		if (res.FrontMesh != null) mi.Mesh = res.FrontMesh;
		if (KeepBackHalf && res.BackMesh != null)
		{
			var back = new MeshInstance3D { Mesh = res.BackMesh, GlobalTransform = mi.GlobalTransform };
			mi.GetParent().AddChild(back);
		}

		// collect raw section data
		if (res.SectionSegments != null && res.SectionSegments.Count > 0)
		{
			foreach (var seg in res.SectionSegments)
				segmentsWorld.Add((mi.GlobalTransform * seg.A, mi.GlobalTransform * seg.B));
		}
		else if (res.SectionLoop != null && res.SectionLoop.Count > 0)
		{
			// legacy point-bag: still add to cap for visuals
			foreach (var p in res.SectionLoop)
				capPointsWorld.Add(mi.GlobalTransform * p);
		}
	}

	// ----------------------------
	// PROJECT → 2D & BUILD LOOPS
	// ----------------------------
	var segs2D = new List<(Vector2 A, Vector2 B)>(segmentsWorld.Count);
	foreach (var (A, B) in segmentsWorld)
	{
		Vector3 ra = A - _aimOrigin;
		Vector3 rb = B - _aimOrigin;
		float ua = ra.Dot(_basisU), va = ra.Dot(_basisV);
		float ub = rb.Dot(_basisU), vb = rb.Dot(_basisV);

		// Flip V for "up is +Y" in 2D and apply scale
		segs2D.Add((new Vector2(ua / SliceUnitsPerPixel, -va / SliceUnitsPerPixel),
					new Vector2(ub / SliceUnitsPerPixel, -vb / SliceUnitsPerPixel)));
	}

	// Assemble closed loops from segments
	var loops = BuildLoopsFromSegments2D(segs2D, eps: 0.5f); // tweak eps if needed

	// Classify outer vs holes by area & containment
	// Convention: positive Area2D => CCW. Depending on your projection flip,
	// you might find holes come out as positive; we’ll still double-check by containment.
	var outers = new List<List<Vector2>>();
	var holes  = new List<List<Vector2>>();

	foreach (var loop in loops)
	{
		var clean = loop;
		if (clean.Count < 3) continue;

		float area = Area2D(clean);
		// normalize: ensure "outer" are CCW (positive)
		if (area < 0f) clean.Reverse();

		outers.Add(clean);
	}

	// Re-run containment to split inner “rings” as holes:
	// A loop is a hole if it lies inside some larger outer and has opposite winding.
	// (Because we reversed everything to CCW above, “holes” will naturally appear CW if we detect them raw.)
	// We’ll test each loop against all others by area & containment.
	var confirmedOuters = new List<List<Vector2>>();
	var holeBuckets = new Dictionary<int, List<Vector2[]>>(); // outerIndex -> holes

	// Sort by absolute area descending so big boundaries get considered first
	var sorted = outers
		.Select((poly, idx) => (poly, idx, area: Mathf.Abs(Area2D(poly))))
		.OrderByDescending(x => x.area)
		.ToList();

	for (int i = 0; i < sorted.Count; i++)
	{
		var (polyI, idxI, _) = sorted[i];

		bool containedByAnother = false;
		for (int j = 0; j < sorted.Count; j++)
		{
			if (i == j) continue;
			var (polyJ, idxJ, _) = sorted[j];
			// quick bbox check can be added; here we just test centroid
			Vector2 centroid = Vector2.Zero;
			foreach (var p in polyI) centroid += p; centroid /= polyI.Count;

			if (PointInPoly(centroid, polyJ))
			{
				// polyI is a HOLE inside polyJ
				containedByAnother = true;
				if (!holeBuckets.TryGetValue(idxJ, out var list)) { list = new List<Vector2[]>(); holeBuckets[idxJ] = list; }
				list.Add(polyI.ToArray());
				break;
			}
		}

		if (!containedByAnother)
		{
			confirmedOuters.Add(polyI);
		}
	}

	// Subtract holes from each outer using Geometry2D.ClipPolygons
	var finalPolys = new List<List<Vector2>>();
	foreach (var outer in confirmedOuters)
	{
		// find original index of this outer to fetch its holes
		int outerIndex = outers.IndexOf(outer);
		var holesForOuter = holeBuckets.TryGetValue(outerIndex, out var hs) ? hs : new List<Vector2[]>();

		var solids = SubtractHoles(outer.ToArray(), holesForOuter);
		foreach (var s in solids)
			if (s != null && s.Length >= 3)
				finalPolys.Add(new List<Vector2>(s));
	}

	// If nothing classified, fall back to all assembled loops
	if (finalPolys.Count == 0)
		finalPolys = loops;

	// Optional visual cap (3D) from whatever world points we gathered
	if (ShowCap && capPointsWorld.Count >= 3)
	{
		var cap = BuildCap(worldPlane, capPointsWorld, CapMaterial);
		if (cap != null) AddChild(cap);
	}

	// ----------------------------
	// HAND OFF TO 2D
	// ----------------------------
	var segments2D = new List<Vector2[]>(); // you can pass thin edges too, if desired

	LaunchSlice2D(finalPolys, segments2D);
}


	// ------------------------------------------------------------
	// 2D scene launch / return
	// ------------------------------------------------------------
	private void LaunchSlice2D(List<List<Vector2>> polygons2D, List<Vector2[]> segments2D)
{
	GD.Print("[SliceManager DEBUG] LaunchSlice2D: path = ", Slice2DScenePath);

	// --- Safety checks ---
	if (_player == null)
	{
		GD.PushError("[SliceManager ERROR] _player is null in LaunchSlice2D. Did you assign PlayerPath to your Player1 node in the inspector?");
		return;
	}

	var scene = GD.Load<PackedScene>(Slice2DScenePath);
	if (scene == null)
	{
		GD.PushError($"[SliceManager ERROR] Cannot load 2D slice scene at '{Slice2DScenePath}'.");
		return;
	}

	var instance = scene.Instantiate();
	if (instance is not SliceLevel2D level)
	{
		GD.PushError("[SliceManager ERROR] Root of Slice2D.tscn is not SliceLevel2D. Check the script on the root Node2D.");
		return;
	}

	// Turn off 3D camera while in 2D
	if (_cam != null)
		_cam.Current = false;

	// Pack 2D geometry
	level.Polygons2D = polygons2D ?? new();
	level.Segments2D = segments2D ?? new();

	// --- Compute player start in slice-space ---
	// Plane is defined by the aim normal and origin we captured earlier
	Plane plane = new Plane(_aimNormal, _aimOrigin.Dot(_aimNormal));
	Vector3 playerPos3D = _player.GlobalPosition;
	Vector3 projected = ProjectPointToPlane(playerPos3D, plane);
	Vector2 start2D = ProjectWorldTo2D(projected);

	level.PlayerStart2D = start2D;

	GD.Print($"[SliceManager DEBUG] PlayerStart2D={start2D}");

	// --- Add the 2D level to the tree ---
	Node rootFor2D = GetTree().CurrentScene ?? GetTree().Root;
	if (rootFor2D == null)
	{
		GD.PushError("[SliceManager ERROR] No valid scene root to add SliceLevel2D to.");
		return;
	}

	rootFor2D.AddChild(level);

	// Hide / freeze the 3D player while in 2D
	_player.Visible = false;
	if (_player is Player1 p1)
		p1.FreezeMotion(true);

	// Connect the exit signal so we can map the movement back to 3D
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
	
	
	_player.Velocity = Vector3.Zero; // optional safety
	_player.SetMeta("justReturnedFromSlice", false);


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

// ---- 2D helpers for assembling loops & handling holes ----

private static float Area2D(IReadOnlyList<Vector2> pts)
{
	if (pts == null || pts.Count < 3) return 0f;
	double s = 0;
	for (int i = 0; i < pts.Count; i++)
	{
		var a = pts[i];
		var b = pts[(i + 1) % pts.Count];
		s += (double)a.X * b.Y - (double)a.Y * b.X;
	}
	return (float)(0.5 * s);
}

// Small grid snap to merge nearly-equal vertices after projection.
private static Vector2 Quantize(Vector2 p, float eps)
{
	float k = 1f / eps;
	return new Vector2(Mathf.Round(p.X * k) / k, Mathf.Round(p.Y * k) / k);
}

// Greedy loop assembly from a bag of 2-point segments (already snapped).
private static List<List<Vector2>> BuildLoopsFromSegments2D(List<(Vector2 A, Vector2 B)> segs, float eps = 0.25f)
{
	var loops = new List<List<Vector2>>();
	if (segs == null || segs.Count == 0) return loops;

	// Map endpoint -> list of outgoing segments indices
	var byPoint = new Dictionary<Vector2, List<int>>();
	var used = new bool[segs.Count];

	for (int i = 0; i < segs.Count; i++)
	{
		var a = Quantize(segs[i].A, eps);
		var b = Quantize(segs[i].B, eps);
		segs[i] = (a, b);

		if (!byPoint.TryGetValue(a, out var la)) { la = new List<int>(); byPoint[a] = la; }
		if (!byPoint.TryGetValue(b, out var lb)) { lb = new List<int>(); byPoint[b] = lb; }
		la.Add(i); lb.Add(i);
	}

	for (;;)
	{
		// pick an unused segment to start a loop
		int start = -1;
		for (int i = 0; i < segs.Count; i++) { if (!used[i]) { start = i; break; } }
		if (start == -1) break;

		var a0 = segs[start].A;
		var b0 = segs[start].B;

		var loop = new List<Vector2> { a0, b0 };
		used[start] = true;

		Vector2 curr = b0;
		// walk forward until we close
		while (!curr.IsEqualApprox(a0))
		{
			if (!byPoint.TryGetValue(curr, out var cand)) break;

			int pick = -1;
			foreach (var idx in cand) if (!used[idx]) { pick = idx; break; }
			if (pick == -1) break;

			var s = segs[pick];
			Vector2 next = s.A.IsEqualApprox(curr) ? s.B : s.A;
			used[pick] = true;
			curr = next;

			// avoid duplicate last vertex
			if (!curr.IsEqualApprox(loop[^1]))
				loop.Add(curr);

			// guard against bad data
			if (loop.Count > 4096) break;
		}

		// only accept proper loop
		if (loop.Count >= 3 && loop[0].IsEqualApprox(loop[^1]) == false && loop[0].IsEqualApprox(loop[^1]) == false)
		{
			// If it didn’t return exactly to a0 (tiny drift), snap it:
			if (!curr.IsEqualApprox(a0))
				loop.Add(a0);
		}

		// basic de-dup
		if (loop.Count >= 3) loops.Add(loop);
	}

	return loops;
}

// Simple point-in-polygon for containment test (even-odd).
private static bool PointInPoly(Vector2 p, IReadOnlyList<Vector2> poly)
{
	bool inside = false;
	for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
	{
		var a = poly[i]; var b = poly[j];
		bool intersect = ((a.Y > p.Y) != (b.Y > p.Y)) &&
						 (p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y + 1e-20f) + a.X);
		if (intersect) inside = !inside;
	}
	return inside;
}

// Subtract all holes that lie inside an outer polygon using Geometry2D.ClipPolygons.
private static List<Vector2[]> SubtractHoles(Vector2[] outer, List<Vector2[]> holes)
{
	// Godot expects CW/CCW consistently. For ClipPolygons you can feed outer as given,
	// it returns an Array<Vector2[]> of resulting simple polygons.
	var solids = new List<Vector2[]>() { outer };

	foreach (var h in holes)
	{
		var next = new List<Vector2[]>();
		foreach (var s in solids)
		{
			var arr = Geometry2D.ClipPolygons(s, h); // subtract h from s
			if (arr != null && arr.Count > 0)
				next.AddRange(arr);
		}
		solids = next;
		if (solids.Count == 0) break;
	}
	return solids;
}



}
