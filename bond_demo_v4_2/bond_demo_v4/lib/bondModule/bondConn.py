import json
import pandas as pd
if __name__ == "__main__":
    from BondGraph.simulator import simulateManager
else:
    from .BondGraph.simulator import simulateManager
from datetime import datetime
import traceback

def get_simulator(smltJson):
    """初始化获取仿真器
    """
    smlt = simulateManager.load(smltJson)
    return smlt

def simulate_result(smlt, data):
    """步进式推演
    """
    t_act = data["crafttime"]
    try:
        if t_act > (smlt.t_pre or 0):
            smlt._solver.set_h(t_act-smlt.t_pre if not smlt.t_pre is None else 1e-9)
            _ = smlt.refresh(data)
        smlt.t_pre  = t_act
        return smlt._renderState()
    except:
        print(f"BOND - - [{datetime.now().strftime('%Y/%m/%d %H:%M:%S')}] \"ERROR | Failed at {t_act}s\" 500 -")
        traceback.print_exc()
        raise Exception(f"[Bond Simulation Error] Failed at {t_act}s") 

if __name__ == "__main__":
    with open(r"..\..\demo\蒸汽发生器键合图模型.json", "r+", encoding="utf-8") as f:
        struct = json.load(f)
    data = [{"crafttime": crafttime, **itm} for crafttime, itm in pd.read_csv(r"..\..\demo\蒸汽发生器键合图模型测试数据.csv", header=0, encoding="gbk").T.to_dict().items()]
    timeIds = [itm["crafttime"] for itm in data]

    smlt = simulateManager(struct)
    smltJson = smlt.save()
    smlt = get_simulator(smltJson)
    for itm in data:
        print("INPUTS:", itm)
        result = simulate_result(smlt, itm)
        print("OUTPUTS:", result)
