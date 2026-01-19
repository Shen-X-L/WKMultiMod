
namespace WKMPMod.Data;

// 封装的读取方法
public static class MPDataSerializer {

	/// <summary>
	/// 序列化到NetDataWriter (无数据包类型)
	/// </summary>
	/// <param name="writer"></param>
	/// <param name="data"></param>
	public static void WriteToNetData(DataWriter writer, PlayerData data) {
		// 基础信息
		writer.Put(data.playId);
		writer.Put(data.TimestampTicks);   // long

		// 变换信息
		writer.Put(data.PosX);
		writer.Put(data.PosY);
		writer.Put(data.PosZ);

		writer.Put(data.RotX);
		writer.Put(data.RotY);
		writer.Put(data.RotZ);
		writer.Put(data.RotW);

		// 左手数据
		writer.Put(data.LeftHand.PosX);
		writer.Put(data.LeftHand.PosY);
		writer.Put(data.LeftHand.PosZ);

		// 右手数据
		writer.Put(data.RightHand.PosX);
		writer.Put(data.RightHand.PosY);
		writer.Put(data.RightHand.PosZ);

		// 状态标志
		writer.Put(data.IsTeleport);
	}

	/// <summary>
	/// 反序列化从NetDataReader (无数据包类型)
	/// </summary>
	/// <param name="reader"></param>
	/// <returns></returns>
	public static PlayerData ReadFromNetData(DataReader reader) {
		var data = new PlayerData();

		data.playId = reader.GetULong();
		data.TimestampTicks = reader.GetLong();

		// 变换信息
		data.PosX = reader.GetFloat();
		data.PosY = reader.GetFloat();
		data.PosZ = reader.GetFloat();

		data.RotX = reader.GetFloat();
		data.RotY = reader.GetFloat();
		data.RotZ = reader.GetFloat();
		data.RotW = reader.GetFloat();

		// 左手数据
		data.LeftHand.PosX = reader.GetFloat();
		data.LeftHand.PosY = reader.GetFloat();
		data.LeftHand.PosZ = reader.GetFloat();


		// 右手数据
		data.RightHand.PosX = reader.GetFloat();
		data.RightHand.PosY = reader.GetFloat();
		data.RightHand.PosZ = reader.GetFloat();


		// 状态标志
		data.IsTeleport = reader.GetBool();

		return data;
	}
}


