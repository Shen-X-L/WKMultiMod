using System;
using System.Collections.Generic;
using System.Text;
using WKMPModa.Shared.Data;

namespace WKMPMod.Data;

public readonly struct NetworkObjectId : IEquatable<NetworkObjectId> {
	public readonly IDType OwnerId;
	public readonly int InstanceId;

	public static IDType UserId;

	public NetworkObjectId(IDType ownerId, int instanceId) {
		OwnerId = ownerId;
		InstanceId = instanceId;
	}

	public NetworkObjectId(int instanceId) {
		OwnerId = UserId;
		InstanceId = instanceId;
	}

	public bool Equals(NetworkObjectId other) =>
		OwnerId.Equals(other.OwnerId) && InstanceId == other.InstanceId;

	public override bool Equals(object obj) =>
		obj is NetworkObjectId other && Equals(other);

	public override int GetHashCode() =>
		HashCode.Combine(OwnerId, InstanceId);

	public override string ToString() {
		return "OwnerId: " + OwnerId + " InstanceId: "+ InstanceId;
	}

	// 元组 → 自定义类型
	public static implicit operator NetworkObjectId((IDType OwnerId, int InstanceId) tuple) {
		return new NetworkObjectId (tuple.OwnerId, tuple.InstanceId);
	}

	// 自定义类型 → 元组
	public static implicit operator (IDType, int)(NetworkObjectId data) {
		return (data.OwnerId, data.InstanceId);
	}

	// == 运算符
	public static bool operator ==(NetworkObjectId left, NetworkObjectId right)
		=> left.Equals(right);

	// != 运算符
	public static bool operator !=(NetworkObjectId left, NetworkObjectId right)
		=> !left.Equals(right);
}