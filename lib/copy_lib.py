import os
import shutil
import sys
from pathlib import Path

import os
import shutil
import sys
from pathlib import Path

def read_md_and_copy(md_file_path, source_dir):
    """
    读取md文件并复制所需dll文件到各文件夹
    
    Args:
        md_file_path: md文件的路径
        source_dir: 源文件目录(游戏Managed目录)
    """
    
    # 检查源目录是否存在
    if not os.path.exists(source_dir):
        print(f"目标文件夹不存在: {source_dir}")
        return
    
    # 解析md文件获取dll列表和文件夹结构
    dlls_by_folder = {}
    current_folder = None
    
    try:
        # RAII
        with open(md_file_path, 'r', encoding='utf-8') as f:
            # 略过第一行
            next(f, None)
            for line in f:
                line = line.strip()
                # 仅匹配'# '开头
                if line.startswith('# ') and not line.startswith('##'):
                    # 获取文件夹名称
                    folder_name = line[2:].strip().replace('\\', '')
                    if folder_name == 'BepInEx':
                        continue
                    current_folder = folder_name
                    if current_folder not in dlls_by_folder:
                        dlls_by_folder[current_folder] = []
                # 仅匹配'- `'开头
                elif line.startswith('- `') and current_folder:
                    dll_name = line.split('`')[1]
                    if dll_name.endswith('.dll'):
                        dlls_by_folder[current_folder].append(dll_name)
    except FileNotFoundError:
        print(f"目标文件不存在: {md_file_path}")
        return
    
    # 获取脚本所在目录
    script_dir = os.path.dirname(os.path.abspath(__file__))
    
    # 记录缺少的dll
    missing_dlls = []
    
    # 为每个文件夹复制文件
    for folder_name, dll_list in dlls_by_folder.items():
        target_folder = os.path.join(script_dir, folder_name)
        
        # 创建目标文件夹
        os.makedirs(target_folder, exist_ok=True)
        
        # 复制每个dll文件
        for dll_file in dll_list:
            source_file = os.path.join(source_dir, dll_file)
            target_file = os.path.join(target_folder, dll_file)
            
            if os.path.exists(source_file):
                shutil.copy2(source_file, target_file)
            else:
                missing_dlls.append(f"{folder_name} 文件夹缺少 {dll_file}")
    
    # 输出结果
    print(f"任务完成")
    for missing in missing_dlls:
        print(missing)

if __name__ == "__main__":
    # 配置变量
    md_file = "README.md"  # md文件路径
    source_directory = r"D:/GAME/Steam/steamapps/common/White Knuckle/White Knuckle_Data/Managed"
    
    read_md_and_copy(md_file, source_directory)