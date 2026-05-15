import os
import re

src_dir = 'src'
command_regex = re.compile(r'(?:public|internal)\s+(?:sealed\s+)?(?:record|class)\s+([A-Za-z0-9_]+).*: IRequest<Result')
validator_regex = re.compile(r'AbstractValidator<([A-Za-z0-9_]+)>')

commands = []

for root, dirs, files in os.walk(src_dir):
    for file in files:
        if file.endswith('.cs'):
            filepath = os.path.join(root, file)
            with open(filepath, 'r', encoding='utf-8') as f:
                content = f.read()
                matches = command_regex.findall(content)
                for match in matches:
                    commands.append((match, filepath))

validators = set()
for root, dirs, files in os.walk(src_dir):
    for file in files:
        if file.endswith('.cs'):
            filepath = os.path.join(root, file)
            with open(filepath, 'r', encoding='utf-8') as f:
                content = f.read()
                matches = validator_regex.findall(content)
                for match in matches:
                    validators.add(match)

for cmd, filepath in commands:
    if cmd not in validators:
        print(f"Missing Validator: {cmd} in {filepath}")
