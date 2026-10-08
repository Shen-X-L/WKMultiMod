using System;
using System.Collections.Generic;
using System.Text;
using WKMPMod.Data;

namespace WKMPMod.Components;

/// <summary>
/// 实体网络同步行为的原子特征接口 (如: 血量同步, Transform 平滑插值, Animator 动画同步等)
/// </summary>
public interface ISyncFeature {
	/// <summary>
	/// 该 Feature 在实体内部的唯一索引编号 (0~255)
	/// </summary>
	byte FeatureIndex { get; set; }
	/// <summary>
	/// 该 Feature 在实体内部的唯一类型/标识标签 (便于查找特定 Feature)
	/// </summary>
	string FeatureId { get; }

	/// <summary>
	/// (主机端) 检查当前 Feature 的数据是否有显著变动 (Dirty 标记), 决定本帧是否需要打包发送
	/// </summary>
	bool IsDirty { get; }

	/// <summary>
	/// (主机端) 将该 Feature 特有的数据序列化写入网络数据包
	/// </summary>
	void WriteState(DataWriter writer);

	/// <summary>
	/// (客户端) 从网络数据包中反序列化并应用该 Feature 的数据
	/// </summary>
	void ReadState(DataReader reader);

	/// <summary>
	/// (客户端) 逐帧更新回调 (用于 Transform 平滑插值, 子部件 Lerp 旋转等)
	/// </summary>
	void OnUpdate(float deltaTime);

	/// <summary>
	/// (可选) 当实体死亡, 注销或断开网络接管时重置内部状态
	/// </summary>
	void OnReset();

	/// <summary>
	/// 当实体销毁, 注销或重新初始化时调用，用于释放静态注册表, 解绑事件等
	/// </summary>
	void OnDestroy();
}