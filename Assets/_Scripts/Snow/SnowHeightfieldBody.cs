using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody))]
public class SnowHeightfieldBody : MonoBehaviour
{
    [Header("Snow Fields")]
    [Tooltip("Optional. If empty, it will auto-find all SnowField in the scene.")]
    [SerializeField] private SnowField[] snowFields;

    [Header("Shape")]
    [SerializeField, Min(0.001f)] private float radius = 0.35f;

    [Header("Probe")]
    [SerializeField, Min(0.1f)] private float probeUp = 2.0f;
    [SerializeField, Min(0.1f)] private float probeDown = 10.0f;

    [Header("Response")]
    [SerializeField, Range(0f, 1f)] private float bounciness = 0.0f;
    [SerializeField, Range(0f, 1f)] private float surfaceFriction = 0.05f;
    [SerializeField] private bool onlyWhenFalling = true;

    [Header("Align To Snow Normal")]
    [SerializeField] private bool alignToNormal = true;
    [SerializeField, Min(0f)] private float alignSpeed = 12f;

    [Header("Field Selection")]
    [Tooltip("Extra meters added to field bounds check in XZ to reduce misses.")]
    [SerializeField, Min(0f)] private float boundsPadding = 2f;

    private Rigidbody rb;

    private struct FieldCol
    {
        public SnowField field;
        public MeshFilter mf;
        public Mesh meshRef;
        public SnowHeightfieldCollider col;

        // Cached world AABB (XZ only used)
        public Vector3 worldMin;
        public Vector3 worldMax;
    }

    private FieldCol[] fieldCols;
    private int lastFieldIndex = -1;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();

        if (snowFields == null || snowFields.Length == 0)
            snowFields = FindObjectsByType<SnowField>(FindObjectsSortMode.None);

        fieldCols = new FieldCol[snowFields.Length];

        for (int i = 0; i < snowFields.Length; i++)
        {
            var f = snowFields[i];
            if (f == null) continue;

            var mf = f.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;

            var mesh = mf.sharedMesh;

            fieldCols[i] = new FieldCol
            {
                field = f,
                mf = mf,
                meshRef = mesh,
                col = new SnowHeightfieldCollider(f.transform, mesh),
            };

            CacheWorldBounds(i);
        }
    }

    private void FixedUpdate()
    {
        int fi = FindFieldIndexFast(rb.position);
        if (fi < 0) return;

        ref FieldCol fc = ref fieldCols[fi];
        if (fc.field == null || fc.mf == null) return;

        // Re-cache bounds if we changed fields (cheap, avoids stale bounds if fields move)
        if (fi != lastFieldIndex)
        {
            CacheWorldBounds(fi);
            lastFieldIndex = fi;
        }

        Mesh mesh = fc.mf.sharedMesh;
        if (mesh == null) return;

        // Rebuild collider ONLY if the mesh reference changed (regenerated)
        if (fc.col == null || fc.meshRef != mesh)
        {
            fc.meshRef = mesh;
            fc.col = new SnowHeightfieldCollider(fc.field.transform, mesh);
        }
        else
        {
            // Non-alloc refresh (requires the collider patch below)
            fc.col.Refresh();
        }

        Vector3 pos = rb.position;
        Vector3 vel = rb.linearVelocity;

        bool falling = Vector3.Dot(vel, Vector3.up) <= 0f;
        if (onlyWhenFalling && !falling)
        {
            // Still correct if already penetrating
            if (!IsPenetrating(fc.col, pos))
                return;
        }

        // Probe down
        Vector3 probeOrigin = pos + Vector3.up * probeUp;
        Ray r = new Ray(probeOrigin, Vector3.down);

        if (!fc.col.Raycast(r, out var hit, probeUp + probeDown))
            return;

        float desiredY = hit.pointWorld.y + radius;
        float penetration = desiredY - pos.y;
        if (penetration <= 0f)
            return;

        pos.y += penetration;
        rb.MovePosition(pos);

        Vector3 n = hit.normalWorld.sqrMagnitude > 1e-6f ? hit.normalWorld.normalized : Vector3.up;

        AlignRotationToNormal(n);

        float vn = Vector3.Dot(vel, n);
        if (vn < 0f)
        {
            vel = vel - vn * n;
            vel = vel + (-vn * bounciness) * n;
        }

        Vector3 tangential = Vector3.ProjectOnPlane(vel, n);
        vel -= tangential * surfaceFriction;

        rb.linearVelocity = vel;
    }

    private void CacheWorldBounds(int i)
    {
        ref FieldCol fc = ref fieldCols[i];
        if (fc.field == null || fc.mf == null || fc.mf.sharedMesh == null) return;

        // Transform mesh local bounds to world AABB (safe even if rotated, via 8 corners)
        Bounds b = fc.mf.sharedMesh.bounds;

        Transform t = fc.field.transform;

        Vector3 c = b.center;
        Vector3 e = b.extents;

        Vector3[] corners = new Vector3[8]
        {
            c + new Vector3(-e.x, -e.y, -e.z),
            c + new Vector3(-e.x, -e.y,  e.z),
            c + new Vector3(-e.x,  e.y, -e.z),
            c + new Vector3(-e.x,  e.y,  e.z),
            c + new Vector3( e.x, -e.y, -e.z),
            c + new Vector3( e.x, -e.y,  e.z),
            c + new Vector3( e.x,  e.y, -e.z),
            c + new Vector3( e.x,  e.y,  e.z),
        };

        Vector3 wMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 wMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        for (int k = 0; k < 8; k++)
        {
            Vector3 w = t.TransformPoint(corners[k]);
            wMin = Vector3.Min(wMin, w);
            wMax = Vector3.Max(wMax, w);
        }

        fc.worldMin = wMin;
        fc.worldMax = wMax;
    }

    private int FindFieldIndexFast(Vector3 worldPos)
    {
        // First: try last field (best case)
        if (lastFieldIndex >= 0 && lastFieldIndex < fieldCols.Length)
        {
            if (WithinXZ(ref fieldCols[lastFieldIndex], worldPos))
                return lastFieldIndex;
        }

        // Next: cheap bounds filter in XZ
        for (int i = 0; i < fieldCols.Length; i++)
        {
            if (fieldCols[i].field == null) continue;
            if (fieldCols[i].mf == null || fieldCols[i].mf.sharedMesh == null) continue;

            if (!WithinXZ(ref fieldCols[i], worldPos))
                continue;

            return i;
        }

        return -1;
    }

    private bool WithinXZ(ref FieldCol fc, Vector3 p)
    {
        float pad = boundsPadding;
        return (p.x >= fc.worldMin.x - pad && p.x <= fc.worldMax.x + pad &&
                p.z >= fc.worldMin.z - pad && p.z <= fc.worldMax.z + pad);
    }

    private bool IsPenetrating(SnowHeightfieldCollider col, Vector3 center)
    {
        Vector3 bottom = center - Vector3.up * radius;
        return col.ContainsPoint(bottom);
    }

    private void AlignRotationToNormal(Vector3 normalWorld)
    {
        if (!alignToNormal) return;

        Vector3 n = normalWorld.sqrMagnitude > 1e-6f ? normalWorld.normalized : Vector3.up;
        if (Vector3.Dot(n, Vector3.up) < 0f) n = -n;

        Vector3 fwd = rb.rotation * Vector3.forward;
        Vector3 fwdOnPlane = Vector3.ProjectOnPlane(fwd, n);

        if (fwdOnPlane.sqrMagnitude < 1e-6f)
        {
            Vector3 right = rb.rotation * Vector3.right;
            fwdOnPlane = Vector3.ProjectOnPlane(right, n);
            if (fwdOnPlane.sqrMagnitude < 1e-6f)
                fwdOnPlane = Vector3.ProjectOnPlane(Vector3.forward, n);
        }

        fwdOnPlane.Normalize();

        Quaternion target = Quaternion.LookRotation(fwdOnPlane, n);
        Quaternion blended = Quaternion.Slerp(rb.rotation, target, 1f - Mathf.Exp(-alignSpeed * Time.fixedDeltaTime));
        rb.MoveRotation(blended);
    }
}
