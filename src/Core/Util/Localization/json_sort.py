import json
from pathlib import Path
from deepdiff import DeepDiff

def get_script_dir():
    """使用 pathlib 获取脚本所在目录"""
    return Path(__file__).parent.absolute()

def sort_json_file(input_file, output_file=None):
    """对JSON文件中的键进行排序"""
    # 获取脚本所在目录
    script_dir = get_script_dir()
    
    # 构建完整的输入文件路径
    input_path = script_dir / input_file

    # 读取JSON文件
    with open(input_file, 'r', encoding='utf-8') as f:
        data = json.load(f)
    
    # 递归排序函数
    def sort_keys(obj):
        if isinstance(obj, dict):
            return {k: sort_keys(v) for k, v in sorted(obj.items())}
        elif isinstance(obj, list):
            return [sort_keys(item) for item in obj]
        return obj
    
    # 排序
    sorted_data = sort_keys(data)
    
    # 确定输出文件路径
    if output_file is None:
        output_path = input_path
    else:
        output_path = script_dir / output_file

    # 保存~
    output = output_file or input_file
    with open(output, 'w', encoding='utf-8') as f:
        json.dump(sorted_data, f, ensure_ascii=False, indent=2)
    
    print(f"已保存到: {output}")

def compare_json_files_simple(file1, file2, ignore_paths=None):
    """
    简化版本：只支持完全路径匹配
    
    Args:
        file1: 第一个文件路径
        file2: 第二个文件路径
        ignore_paths: 要忽略的路径列表, 格式如：
            ["0_DisplayMessage"]  # 忽略整个根键
            ["0_DisplayMessage.JoinMessages"]  # 忽略嵌套路径
    """
    if ignore_paths is None:
        ignore_paths = []
    
    ignore_set = set(ignore_paths)
    
    with open(file1, 'r', encoding='utf-8') as f:
        data1 = json.load(f)
    with open(file2, 'r', encoding='utf-8') as f:
        data2 = json.load(f)
    
    def should_ignore_path(current_path: str) -> bool:
        """检查路径是否应该被忽略"""
        return current_path in ignore_set
    
    def remove_keys_by_path(obj, current_path=""):
        """根据路径递归删除要忽略的键"""
        if isinstance(obj, dict):
            result = {}
            for key, value in obj.items():
                new_path = f"{current_path}.{key}" if current_path else key
                
                if should_ignore_path(new_path):
                    continue
                
                result[key] = remove_keys_by_path(value, new_path)
            return result
        
        elif isinstance(obj, list):
            return [remove_keys_by_path(item, current_path) for item in obj]
        else:
            return obj
    
    data1_filtered = remove_keys_by_path(data1)
    data2_filtered = remove_keys_by_path(data2)
    
    diff = DeepDiff(
        data1_filtered, data2_filtered,
        ignore_order=True,
        ignore_string_type_changes=True,
        ignore_numeric_type_changes=True,
        significant_digits=0
    )
    
    result = {}
    for key in ['dictionary_item_removed', 'dictionary_item_added', 
                'type_changes', 'iterable_item_removed', 'iterable_item_added']:
        if key in diff:
            result[key] = diff[key]
    
    return result

# 使用示例
if __name__ == "__main__":
    # 对json文件排序
    sort_json_file("texts_zh.json")
    sort_json_file("texts_en.json")
    
    # 对比json结构
    result = compare_json_files_simple("texts_zh.json","texts_en.json",ignore_paths=["0_DeathMessage","0_DisplayMessage"])
    
    if result:
        print("{")
        for key, value in result.items():
            print(f"  '{key}':")
            if isinstance(value, dict):
                for sub_key, sub_val in value.items():
                    # 处理多行字符串
                    if isinstance(sub_val, str):
                        sub_val = ' '.join(sub_val.split())
                    print(f"    {sub_key}: {sub_val}")
            elif isinstance(value, list):
                for item in value:
                    if isinstance(item, str):
                        item = ' '.join(item.split())
                    print(f"    {item}")
            else:
                if isinstance(value, str):
                    value = ' '.join(value.split())
                print(f"    {value}")
        print("}")
    
