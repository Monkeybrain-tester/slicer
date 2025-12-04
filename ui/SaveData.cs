using System;
using System.Collections.Generic;

[Serializable]
public class SaveData
{
	// Highest unlocked level index, 1-based. Level 1 is unlocked by default.
	public int HighestUnlockedLevel = 1;

	// Optional: which levels have actually been completed at least once.
	public List<int> CompletedLevels = new();
}
