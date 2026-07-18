using UnityEngine;

public class ObjectRotationSyncWithYOffset : MonoBehaviour
{
    public Transform target; 
    public float yOffset = 90f; 

    void Update()
    {
        if (target != null)
        {
            
            transform.position = target.position;

            
            Vector3 targetRotation = target.eulerAngles;

            
            transform.rotation = Quaternion.Euler(transform.rotation.eulerAngles.x, targetRotation.y + yOffset, transform.rotation.eulerAngles.z);
        }
    }
}
