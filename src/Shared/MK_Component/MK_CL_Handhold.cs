using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Events;

namespace WKMPMod.Shared.MK_Component;

public class MK_CL_Handhold :MonoBehaviour{
	public UnityEvent activeEvent = new UnityEvent(); // 抓握时触发的事件
	public UnityEvent stopEvent = new UnityEvent(); // 释放时触发的事件
	public Renderer handholdRenderer; // 攀爬点渲染器, 用于高亮攀爬点
}
