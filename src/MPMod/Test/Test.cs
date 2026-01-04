using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using UnityEngine;
using UnityEngine.Events;
using WKMultiMod.src.Component;
using WKMultiMod.src.Core;
using WKMultiMod.src.Util;
using Vector3 = UnityEngine.Vector3;

namespace WKMultiMod.src.Test;

public static class Test {
	public static void Main(string[] arg) {
		var player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
		player.name = "RemoteEntity";

		// 配置触发器
		var collider = player.GetComponent<CapsuleCollider>();
		if (collider != null) {
			collider.isTrigger = true;
			collider.radius = 0.5f;
			collider.height = 2.0f;
		}

		// 添加物理碰撞器
		var physicsCollider = player.AddComponent<CapsuleCollider>();
		physicsCollider.isTrigger = false;
		physicsCollider.radius = 0.4f;
		physicsCollider.height = 1.8f;
		physicsCollider.center = new Vector3(0, 0.1f, 0);

		// 设置材质
		Material bodyMaterial = new Material(Shader.Find("Unlit/Color"));
		bodyMaterial.color = Color.gray;

		Renderer renderer = player.GetComponent<Renderer>();
		if (renderer != null) {
			renderer.material = bodyMaterial;
		}

		// 添加组件
		AddComponents(player);

		player.transform.position = new Vector3(0.0f, 2.0f, 0.0f);
	}
	// 赋予可攀爬组件 和 实体组件 和 标签组件
	public static void AddComponents(GameObject gameObject) {
		// 添加 ObjectTagger 组件
		ObjectTagger tagger = gameObject.AddComponent<ObjectTagger>();
		if (tagger != null) {
			tagger.tags.Add("Handhold");
			tagger.tags.Add("Damageable");
			tagger.tags.Add("Entity");
		}

		// 添加RemoteEntity组件
		var entity = gameObject.AddComponent<RemoteEntity>();
		entity.PlayerId = 0;

		// 添加 CL_Handhold 组件 (攀爬逻辑)
		var handholdComponent = gameObject.AddComponent<CL_Handhold>();
		if (handholdComponent != null) {
			// 添加停止和激活事件
			handholdComponent.stopEvent = new UnityEvent();
			handholdComponent.activeEvent = new UnityEvent();
		}

		// 确保 渲染器 被赋值, 否则 材质 设置会崩溃
		var objectRenderer = gameObject.GetComponent<Renderer>();
		if (objectRenderer != null) {
			gameObject.GetComponent<CL_Handhold>().handholdRenderer = objectRenderer;
		}
	}
}