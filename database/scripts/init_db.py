#!/usr/bin/env python3
"""
数据库初始化脚本
用于创建所有数据库表
"""

import sys
import os

# 添加项目根目录到路径
project_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, project_root)

from backend.app.core.database import engine, Base
from backend.app.models.models import Novel, Character, Chapter

def init_db():
    """初始化数据库，创建所有表"""
    print("正在创建数据库表...")
    Base.metadata.create_all(bind=engine)
    print("数据库表创建成功！")
    
    # 打印创建的表
    print("\n已创建的表:")
    for table in Base.metadata.sorted_tables:
        print(f"  - {table.name}")

if __name__ == "__main__":
    init_db()