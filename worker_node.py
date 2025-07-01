import requests
import time
from datetime import datetime
import sys

class WorkerNode:
    def __init__(self, node_id, server_url='http://localhost:5001', heartbeat_interval=0.5):
        self.node_id = node_id
        self.server_url = server_url
        self.heartbeat_interval = heartbeat_interval
        self.running = True
        self.headers = {
            'Content-Type': 'application/json',
            'Accept': 'application/json',
            'X-Requested-With': 'XMLHttpRequest'
        }

    def send_heartbeat(self):
        payload = {
            'node_id': self.node_id,
            'timestamp': datetime.now().isoformat()
        }
        try:
            response = requests.post(
                f"{self.server_url}/heartbeat",
                json=payload,
                headers=self.headers,
                timeout=3
            )
            print(f"[{self.node_id}] Server response: {response.status_code} - {response.text.strip()}")
            return response.status_code == 200
        except requests.exceptions.RequestException as e:
            print(f"[{self.node_id}] Heartbeat failed: {type(e).__name__}: {str(e)}")
            return False

    def run(self):
        print(f"[{self.node_id}] Starting worker node (Heartbeat interval: {self.heartbeat_interval}s)")
        
        while self.running:
            success = self.send_heartbeat()
            if not success:
                print(f"[{self.node_id}] Failed to send heartbeat")
            
            time.sleep(self.heartbeat_interval)

if __name__ == '__main__':
    if len(sys.argv) < 2:
        print("Usage: python worker_node.py <node_id> [heartbeat_interval]")
        sys.exit(1)
    
    node_id = sys.argv[1]
    interval = float(sys.argv[2]) if len(sys.argv) > 2 else 0.5
    
    worker = WorkerNode(node_id, heartbeat_interval=interval)
    
    try:
        worker.run()
    except KeyboardInterrupt:
        worker.running = False
        print(f"\n[{node_id}] Worker node stopped")
