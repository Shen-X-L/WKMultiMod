using DarkMachine.AI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Unity.Entities;
using UnityEngine;
using WKMPMod.Data;

namespace WKMPMod.Components;


public class AttackSyncFeature : ISyncFeature, ICustomSyncFeature {
	#region[ISyncFeature接口实现]

	public byte FeatureIndex { get; set; }

	public string FeatureId { get; }

	public bool IsDirty => false;

	// 该 Feature 当前不参与常规状态序列化 完整同步时也必须安全地写入/读取空状态
	public void WriteState(DataWriter writer) { }
	public void ReadState(DataReader reader) { }
	public void OnUpdate(float deltaTime) { }
	public void OnReset() { }

	public void OnDestroy() {

	}

	#endregion

	#region[ICustomSyncFeature接口实现]

	public void HandleCustomMessage(ulong senderId, DataReader reader, bool needReply) {

	}

	public void HandleCustomReply(IDType senderId) {

	}

	#endregion
}
