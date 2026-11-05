using System;
using System.Collections.Generic;
using System.Text;
using WKMPMod.Data;

namespace WKMPMod.Components;

/// <summary>
/// 实体网络同步行为的原子特征接口
/// #region[ISyncFeature 基础实现]
/// </summary>
public interface ISyncFeature {
	/// <summary>
	/// 该 Feature 在实体内部的唯一类型/标识标签 (便于查找特定 Feature)
	/// </summary>
	string FeatureId { get; }

	/// <summary>
	/// (可选) 当实体死亡, 注销或断开网络接管时重置内部状态
	/// </summary>
	void OnReset();

	/// <summary>
	/// 当实体销毁, 注销或重新初始化时调用, 用于释放静态注册表, 解绑事件等
	/// </summary>
	void OnDestroy();
}

/// <summary>
/// #region[IStateSyncFeature 状态同步实现]
/// </summary>
public interface IStateSyncFeature : ISyncFeature {
	/// <summary>
	/// 该 StateFeature 在实体内部的唯一索引编号 (0~255)
	/// </summary>
	byte StateIndex { get; set; }

	/// <summary>
	/// 所有者 检查当前 Feature 的数据是否有显著变动 (Dirty 标记), 决定本帧是否需要打包发送
	/// </summary>
	bool IsDirty { get; }

	/// <summary>
	/// 所有者 将该 Feature 特有的数据序列化写入网络数据包
	/// </summary>
	void WriteState(DataWriter writer);

	///// <summary>
	///// 所有者 将该 Feature 特有的全状态数据序列化写入网络数据包给新订阅者
	///// </summary>
	//void WriteFullState(DataWriter writer);

	/// <summary>
	/// 订阅者 从网络数据包中反序列化并应用该 Feature 的数据
	/// </summary>
	void ReadState(DataReader reader);
}

/// <summary>
/// #region[IFrameUpdateFeature 帧更新实现]
/// </summary>
public interface IFrameUpdateFeature : ISyncFeature {
	/// <summary>
	/// 逐帧更新回调 (用于 Transform 平滑插值, 子部件 Lerp 旋转等)
	/// </summary>
	void OnUpdate(float deltaTime);
}

/// <summary>
/// #region[ICustomSyncFeature 信号式交互实现]
/// </summary>
public interface ICustomSyncFeature : ISyncFeature {
	/// <summary>
	/// 该 CustomFeature 在实体内部的唯一索引编号 (0~255)
	/// </summary>
	byte CustomIndex { get; set; }
	/// <summary>
	/// 收到针对该 Feature 的自定义消息。
	/// </summary>
	void HandleCustomMessage(IDType senderId, DataReader reader, bool needReply);

	/// <summary>
	/// 收到目标实体或 Feature 不存在的失败响应。
	/// </summary>
	void HandleCustomReply(IDType senderId);
}