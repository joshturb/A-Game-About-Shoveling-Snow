using UnityEngine;

public class RigidbodyPusher : MonoBehaviour
{
    [Header("Push")]
    public float pushPower = 2.0f;

    [Header("Ground Align (4-corner average normal)")]
    [SerializeField] private bool alignToGround = true;
    [SerializeField] private LayerMask groundMask = ~0;
    [SerializeField, Min(0.01f)] private float rayUp = 0.5f;
    [SerializeField, Min(0.05f)] private float rayDown = 3.0f;
    [SerializeField, Range(0.1f, 1.0f)] private float cornerExtentScale = 0.9f;
    [SerializeField, Min(0f)] private float alignSpeed = 12f;

    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        Rigidbody body = hit.collider.attachedRigidbody;

        if (body == null || body.isKinematic)
            return;

        if (hit.moveDirection.y < -0.3f)
            return;

        Vector3 pushDir = new Vector3(hit.moveDirection.x, 0f, hit.moveDirection.z);
        body.linearVelocity = pushDir * pushPower;

        if (alignToGround)
            AlignRigidBodyToAveragedCornerNormal(body);
    }

    private void AlignRigidBodyToAveragedCornerNormal(Rigidbody body)
    {
        Collider col = body.GetComponent<Collider>();
        if (col == null)
            return;

        Bounds b = col.bounds;

        Vector3 center = b.center;
        Vector3 ext = b.extents;

        // Use body orientation for corners (stable on rotated objects)
        Vector3 right = body.transform.right;
        Vector3 fwd = body.transform.forward;

        float ex = ext.x * cornerExtentScale;
        float ez = ext.z * cornerExtentScale;

        Vector3[] corners =
        {
            center + right * ex + fwd * ez,
            center + right * ex - fwd * ez,
            center - right * ex + fwd * ez,
            center - right * ex - fwd * ez
        };

        Vector3 nSum = Vector3.zero;
        int nCount = 0;

        for (int i = 0; i < 4; i++)
        {
            Vector3 origin = corners[i] + Vector3.up * rayUp;
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit h, rayUp + rayDown, groundMask, QueryTriggerInteraction.Ignore))
            {
                Vector3 n = h.normal;
                if (n.sqrMagnitude > 1e-6f)
                {
                    if (Vector3.Dot(n, Vector3.up) < 0f) n = -n;
                    nSum += n.normalized;
                    nCount++;
                }
            }
        }

        if (nCount == 0)
            return;

        Vector3 up = (nSum / nCount).normalized;
        if (up.sqrMagnitude < 1e-6f)
            return;

        // Preserve facing by projecting current forward onto the averaged plane
        Vector3 curFwd = body.rotation * Vector3.forward;
        Vector3 fwdOnPlane = Vector3.ProjectOnPlane(curFwd, up);

        if (fwdOnPlane.sqrMagnitude < 1e-6f)
        {
            Vector3 curRight = body.rotation * Vector3.right;
            fwdOnPlane = Vector3.ProjectOnPlane(curRight, up);
            if (fwdOnPlane.sqrMagnitude < 1e-6f)
                return;
        }

        fwdOnPlane.Normalize();

        Quaternion target = Quaternion.LookRotation(fwdOnPlane, up);
        float t = 1f - Mathf.Exp(-alignSpeed * Time.deltaTime);
        body.MoveRotation(Quaternion.Slerp(body.rotation, target, t));
    }
}
