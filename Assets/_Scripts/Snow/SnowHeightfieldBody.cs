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

    [Header("Normal Sampling (4 Corners)")]
    [Tooltip("XZ half-size of the 4-corner sample square (world units). If 0, uses radius.")]
    [SerializeField, Min(0f)] private float normalSampleRadius = 0f;

    [Header("Upright Clamp")]
    [SerializeField, Min(0f)] private float maxAlignTiltDegrees = 65f;
    [SerializeField, Min(0f)] private float uprightSpeed = 10f;

    [Header("Stop Align When Still")]
    [Tooltip("If XZ speed rises above this, alignment is enabled.")]
    [SerializeField, Min(0f)] private float alignStartSpeedXZ = 0.35f;
    [Tooltip("If XZ speed falls below this, alignment is disabled (hysteresis).")]
    [SerializeField, Min(0f)] private float alignStopSpeedXZ = 0.20f;
    [Tooltip("When not aligning, damp angular velocity to prevent rocking.")]
    [SerializeField, Range(0f, 1f)] private float stillAngularDamping = 0.25f;
    [Tooltip("Extra smoothing on the averaged normal to reduce chatter.")]
    [SerializeField, Min(0f)] private float normalSmoothSpeed = 10f;

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

        public Vector3 worldMin;
        public Vector3 worldMax;
    }

    private FieldCol[] fieldCols;
    private int lastFieldIndex = -1;

    private bool doAlign;
    private Vector3 smoothedNormal = Vector3.up;

    private void Start()
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
                col = f._hf
            };

            CacheWorldBounds(i);
        }

        doAlign = true;
        smoothedNormal = Vector3.up;
    }

    private void FixedUpdate()
    {
        int fi = FindFieldIndexFast(rb.position);
        if (fi < 0) return;

        ref FieldCol fc = ref fieldCols[fi];
        if (fc.field == null || fc.mf == null) return;

        if (fi != lastFieldIndex)
        {
            CacheWorldBounds(fi);
            lastFieldIndex = fi;
        }

        Mesh mesh = fc.mf.sharedMesh;
        if (mesh == null) return;

        if (fc.col == null || fc.meshRef != mesh)
        {
            fc.meshRef = mesh;
            fc.col = new SnowHeightfieldCollider(fc.field.transform, mesh);
        }
        else
        {
            fc.col.Refresh(fc.field.GetVerts());
        }

        Vector3 pos = rb.position;
        Vector3 vel = rb.linearVelocity;

        bool falling = Vector3.Dot(vel, Vector3.up) <= 0f;
        if (onlyWhenFalling && !falling)
        {
            if (!IsPenetrating(fc.col, pos))
                return;
        }

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

        // --- stop alignment when still (XZ speed hysteresis) ---
        Vector3 velXZ = new Vector3(vel.x, 0f, vel.z);
        float speedXZ = velXZ.magnitude;

        if (doAlign)
            doAlign = speedXZ > alignStopSpeedXZ;
        else
            doAlign = speedXZ >= alignStartSpeedXZ;

        // 4-corner averaged normal around the hit point (XZ)
        Vector3 n = GetAveragedCornerNormal(fc.col, hit.pointWorld);

        // Smooth the normal to reduce chatter (only really matters when aligning).
        float aN = 1f - Mathf.Exp(-normalSmoothSpeed * Time.fixedDeltaTime);
        smoothedNormal = Vector3.Slerp(smoothedNormal, n, aN);
        if (smoothedNormal.sqrMagnitude < 1e-6f) smoothedNormal = Vector3.up;
        smoothedNormal.Normalize();

        AlignRotationToNormal(smoothedNormal, doAlign);

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

    private Vector3 GetAveragedCornerNormal(SnowHeightfieldCollider col, Vector3 centerWorld)
    {
        float s = normalSampleRadius > 0f ? normalSampleRadius : radius;

        Vector3 sum = Vector3.zero;
        int count = 0;

        Vector3 o0 = new Vector3(-s, 0f, -s);
        Vector3 o1 = new Vector3(-s, 0f,  s);
        Vector3 o2 = new Vector3( s, 0f, -s);
        Vector3 o3 = new Vector3( s, 0f,  s);

        Vector3 up = Vector3.up * probeUp;
        float len = probeUp + probeDown;

        SampleCorner(col, centerWorld + o0, up, len, ref sum, ref count);
        SampleCorner(col, centerWorld + o1, up, len, ref sum, ref count);
        SampleCorner(col, centerWorld + o2, up, len, ref sum, ref count);
        SampleCorner(col, centerWorld + o3, up, len, ref sum, ref count);

        if (count == 0)
            return Vector3.up;

        Vector3 n = sum / count;
        if (n.sqrMagnitude < 1e-6f)
            n = Vector3.up;

        n.Normalize();
        if (Vector3.Dot(n, Vector3.up) < 0f) n = -n;
        return n;
    }

    private static void SampleCorner(
        SnowHeightfieldCollider col,
        Vector3 cornerWorld,
        Vector3 upOffset,
        float rayLength,
        ref Vector3 normalSum,
        ref int count)
    {
        Ray rr = new Ray(cornerWorld + upOffset, Vector3.down);
        if (!col.Raycast(rr, out var h, rayLength))
            return;

        Vector3 n = h.normalWorld;
        if (n.sqrMagnitude < 1e-6f)
            return;

        normalSum += n.normalized;
        count++;
    }

    private void CacheWorldBounds(int i)
    {
        ref FieldCol fc = ref fieldCols[i];
        if (fc.field == null || fc.mf == null || fc.mf.sharedMesh == null) return;

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
        if (lastFieldIndex >= 0 && lastFieldIndex < fieldCols.Length)
        {
            if (WithinXZ(ref fieldCols[lastFieldIndex], worldPos))
                return lastFieldIndex;
        }

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

    private void AlignRotationToNormal(Vector3 normalWorld, bool allowAlignNow)
    {
        if (!alignToNormal) return;

        // Always handle extreme tilt: go upright and kill rocking.
        float tilt = Vector3.Angle(rb.rotation * Vector3.up, Vector3.up);
        if (tilt > maxAlignTiltDegrees)
        {
            Quaternion upright = Quaternion.Euler(0f, rb.rotation.eulerAngles.y, 0f);
            Quaternion blendedUpright = Quaternion.Slerp(
                rb.rotation,
                upright,
                1f - Mathf.Exp(-uprightSpeed * Time.fixedDeltaTime)
            );
            rb.MoveRotation(blendedUpright);
            return;
        }

        // When still: do NOT chase normals; just damp angular velocity to stop rocking.
        if (!allowAlignNow)
        {
            rb.angularVelocity *= stillAngularDamping;
            return;
        }

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
