using System;
using System.Collections.Generic;
using System.Text;
using WKMPMod.Data;

namespace WKMPMod.Components;

public class TeethSyncFeature : IStateSyncFeature {

	#region[ISyncFeature 基础实现]

	public string FeatureId => "Teeth";
	public void OnDestroy() {
	}

	public void OnReset() {
	}

	#endregion

	#region[IStateSyncFeature 状态同步实现]

	public byte StateIndex { get ; set ; }

	public bool IsDirty => throw new NotImplementedException();



	public void ReadState(DataReader reader) {
		throw new NotImplementedException();
	}

	public void WriteState(DataWriter writer) {
		throw new NotImplementedException();
	}

	#endregion
}
