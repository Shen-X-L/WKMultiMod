using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Events;

namespace WKMPMod.Shared.MK_Component;

public class MK_CL_Handhold :MonoBehaviour{
	public UnityEvent activeEvent = new UnityEvent();
	public UnityEvent stopEvent = new UnityEvent();
}
