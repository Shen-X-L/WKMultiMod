namespace WKMPMod.Data;

public interface INetworkSerializable {
	void Serialize(DataWriter writer);
	void Deserialize(DataReader reader);
}