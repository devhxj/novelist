import re

with open('backend/app/mcp/router.py', 'r') as f:
    content = f.read()

content = re.sub(
    r'registry\.execute\(\s*"([^"]+)",',
    r'registry.execute(\n        "\1",\n        db=db,',
    content
)

with open('backend/app/mcp/router.py', 'w') as f:
    f.write(content)
