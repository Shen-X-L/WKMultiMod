import json
from pathlib import Path
from deepdiff import DeepDiff
import sys

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

# 仅对比结构(只关心类型和键，不关心值)
def compare_structure(obj1, obj2):
    """对比 JSON 结构"""
    diff = DeepDiff(
        obj1, obj2,
        ignore_order=True,                    # 忽略数组顺序
        ignore_string_type_changes=True,      # 忽略字符串类型
        ignore_numeric_type_changes=True,     # 忽略数字类型差异
        significant_digits=0,                 # 忽略数值精度差异
        exclude_paths=[] if not isinstance(obj1, dict) else []
    )
    
    # 过滤掉值变化的差异，只保留类型变化的差异
    result = {}
    if 'type_changes' in diff:
        result['type_changes'] = diff['type_changes']
    if 'dictionary_item_removed' in diff:
        result['dictionary_item_removed'] = diff['dictionary_item_removed']
    if 'dictionary_item_added' in diff:
        result['dictionary_item_added'] = diff['dictionary_item_added']
    if 'iterable_item_removed' in diff:
        result['iterable_item_removed'] = diff['iterable_item_removed']
    if 'iterable_item_added' in diff:
        result['iterable_item_added'] = diff['iterable_item_added']
    
    return result

def compare_file_structure(input_file1, input_file2):
    # 读取JSON文件
    with open(input_file1, 'r', encoding='utf-8') as f:
        data1 = json.load(f)
    with open(input_file2, 'r', encoding='utf-8') as f:
        data2 = json.load(f)
        
    print(compare_structure(data1,data2))

# 使用示例
if __name__ == "__main__":
    # 对json文件排序
    # sort_json_file("texts_zh.json")
    # sort_json_file("texts_en.json")
    
    # 对比json结构
    compare_file_structure("texts_zh.json","texts_en.json")
    
    



