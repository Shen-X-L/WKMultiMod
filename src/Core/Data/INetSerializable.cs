using System;
using System.Collections.Generic;
using System.Text;
using WKMPMod.Data;

namespace WKMultiPlayerMod.Data;

public interface INetworkSerializable {
	void Serialize(DataWriter writer);
	void Deserialize(DataReader reader);
}