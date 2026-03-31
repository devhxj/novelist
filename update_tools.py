import os
import re
import glob

def process_file(filepath):
    with open(filepath, 'r') as f:
        content = f.read()

    # Replace __init__(self, db: AsyncSession)
    content = re.sub(r'def __init__\(self, db: AsyncSession\):\s+self\.db = db', r'def __init__(self):\n        pass', content)
    
    # Replace execute(self, ..., **kwargs) with execute(self, db: AsyncSession, ..., **kwargs)
    # Wait, execute might have different parameters
    # Let's match: async def execute(self,
    content = re.sub(r'async def execute\(\s*self,\s*', r'async def execute(\n        self, \n        db: AsyncSession,\n        ', content)

    # Replace self.db with db
    content = re.sub(r'self\.db', r'db', content)

    # Replace register_all(db: AsyncSession, registry: MCPToolRegistry) -> None:
    # with register_all(registry: MCPToolRegistry) -> None:
    content = re.sub(r'def register_all\(db: AsyncSession, registry: MCPToolRegistry\)', r'def register_all(registry: MCPToolRegistry)', content)
    
    # In register_all, registry.register(Tool(db)) -> registry.register(Tool())
    content = re.sub(r'registry\.register\(([a-zA-Z0-9_]+)\(db\)\)', r'registry.register(\1())', content)

    with open(filepath, 'w') as f:
        f.write(content)

for filepath in glob.glob("backend/app/mcp/*_tools.py"):
    print("Processing", filepath)
    process_file(filepath)
