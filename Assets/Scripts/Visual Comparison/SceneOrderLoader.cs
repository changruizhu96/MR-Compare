using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;

[System.Serializable]
public class RoomSceneOrder
{
    public List<int> room1;
    public List<int> room2;
}

public class SceneOrderLoader : MonoBehaviour
{
    [Header("Scene Order Parameters")]
    [Range(1, 8)] public int groupNumber = 1;     // Values from 1 to 8
    [Tooltip("Use A, B, C, D, E")]
    public string participants = "A";                // One of: A, B, C, D
    [Range(1, 2)] public int roomNumber = 1;      // 1 or 2
    public string loadedFile;
    [Header("Scene Order Output")]
    public List<int> sceneOrder = new List<int>();
    
    private Dictionary<string, Dictionary<string, RoomSceneOrder>> sceneLookup;
    

    void Start()
    {
        LoadSceneOrder();
    }

    void LoadSceneOrder()
    {
        string path = Path.Combine(Application.dataPath, loadedFile);

        if (!File.Exists(path))
        {
            Debug.LogError("scene_lookup.json not found at: " + path);
            return;
        }

        string json = File.ReadAllText(path);
        sceneLookup = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, RoomSceneOrder>>>(json);

        string groupKey = $"group_{groupNumber}";
        participants = participants.ToUpper().Trim();

        if (!sceneLookup.ContainsKey(groupKey))
        {
            Debug.LogError($"Group '{groupKey}' not found.");
            return;
        }

        if (!sceneLookup[groupKey].ContainsKey(participants))
        {
            Debug.LogError($"participants '{participants}' not found in {groupKey}.");
            return;
        }

        RoomSceneOrder order = sceneLookup[groupKey][participants];
        sceneOrder = (roomNumber == 1) ? order.room1 : order.room2;

        Debug.Log($"Loaded scene order for Group {groupNumber}, participants {participants}, Room {roomNumber}: [{string.Join(",", sceneOrder)}]");
    }
}
