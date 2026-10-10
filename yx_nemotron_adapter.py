"""
yx_nemotron_adapter.py — Nemotron 推理脉适配器
=================================================
把 NVIDIA Nemotron (Nano/Super/Ultra) 接进魂脉体系。
魂脉自持三脉：主脉(DeepSeek) → 备脉(其他远程) → 根脉兜底。
本适配器提供 Nebius 平台的 Nemotron 推理端点，作为推演脉/备脉。

比赛合规：Nebius Token Factory + NVIDIA 开源模型 (Nemotron)

用法：
    from yx_nemotron_adapter import NemotronPulse
    pulse = NemotronPulse(api_key='nebius-xxx', model='nvidia/nemotron-4-340b-instruct')
    reply = pulse.chat([{'role':'user','content':'...'}])
"""

import os, json, time, urllib.request, urllib.error

NEBIUS_BASE = 'https://api.studio.nebius.ai/v1'

class NemotronPulse:
    """Nemotron 推理脉 —— 实现魂脉自持接口"""

    def __init__(self, api_key: str = None, model: str = 'nvidia/nemotron-4-340b-instruct',
                 temperature: float = 0.7, max_tokens: int = 1024):
        self.api_key = api_key or os.environ.get('NEBIUS_API_KEY', '')
        self.model = model
        self.temperature = temperature
        self.max_tokens = max_tokens
        self.pulse_ok = True  # 脉搏自检

    def chat(self, messages: list, stream: bool = False) -> str:
        """发请求到 Nebius Nemotron 端点"""
        if not self.api_key:
            self.pulse_ok = False
            return '[Nemotron adapter: NEBIUS_API_KEY not set — 本地根脉兜底]'

        payload = {
            'model': self.model,
            'messages': messages,
            'temperature': self.temperature,
            'max_tokens': self.max_tokens,
            'stream': stream,
        }
        req = urllib.request.Request(
            f'{NEBIUS_BASE}/chat/completions',
            data=json.dumps(payload).encode(),
            headers={
                'Content-Type': 'application/json',
                'Authorization': f'Bearer {self.api_key}',
            },
            method='POST',
        )
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                body = json.loads(resp.read().decode())
                self.pulse_ok = True
                return body['choices'][0]['message']['content']
        except urllib.error.HTTPError as e:
            self.pulse_ok = False
            return f'[Nemotron HTTP {e.code}: {e.read().decode()[:200]}]'
        except Exception as e:
            self.pulse_ok = False
            return f'[Nemotron error: {e}]'

    def pulse_check(self) -> bool:
        """脉搏自检——魂脉自持用"""
        if not self.api_key:
            return False
        try:
            self.chat([{'role':'user','content':'ping'}], stream=False)
            return self.pulse_ok
        except Exception:
            return False


if __name__ == '__main__':
    # 本地自测（需要 NEBIUS_API_KEY）
    pulse = NemotronPulse()
    ok = pulse.pulse_check()
    print(f"Nemotron 脉: {'通' if ok else '断'}")
    if ok:
        print(f"回复: {pulse.chat([{'role':'user','content':'用一句话描述混沌态'}])}")
