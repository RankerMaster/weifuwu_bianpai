from flask import Flask, request, render_template, jsonify, send_file
import pandas as pd
import os, json
import uuid, time, warnings
from lib.bondModule.bondConn import get_simulator, simulate_result

app = Flask(__name__, static_url_path='/static')

def get_uuid(kw=""):
    return str(uuid.uuid5(uuid.NAMESPACE_DNS, f"{kw}#%.7f"%time.time()))

class mdlMananger():
    def __init__(self):
        self._mdl_stock = {}

    def register(self, config, summary=False):
        uuid_ = get_uuid("bond-model")
        self._mdl_stock[uuid_] = get_simulator(config)
        if summary:
            self._mdl_stock[uuid_].summary()
        return uuid_

    def get_model(self, uuid_):
        return self._mdl_stock[uuid_]

    def restart_model(self, uuid_):
        self._mdl_stock[uuid_].restart()
        return

    def del_model(self, uuid_):
        del self._mdl_stock[uuid_]

    def _save(self):
        if not os.path.exists(r"./static/roaming"):
            os.mkdir(r"./static/roaming")
        for k, mdl in self._mdl_stock:
            with open(rf"./static/roaming/{k}.json", "w+", encoding="utf-8") as f:
                f.write(mdl.save())

    def _load(self):
        if os.path.exists(r"./static/roaming"):
            for k in os.listdir(r"./static/roaming"):
                with open(rf"./static/roaming/{k}", "w+", encoding="utf-8") as f:
                    self._mdl_stock[k[:-5]] = get_simulator(f.read())

    def __del__(self):
        self._save()
        
MODELMANAGER = mdlMananger()

@ app.route('/bond-graph/build-simu-task',methods=['POST'])
def build_simu_task(kw=""):
    """ POST
    [DATA]
        `modelStruct`: str, 键合图所导出的模型JSON
    [RETURN]
        modelUid: str, 键合图模型标记（失败则返回null）
    """
    try:
        print("build simu task get request")
        modelStruct = request.form.get('modelStruct')
        modelUid = MODELMANAGER.register(modelStruct, summary=False)
        return modelUid
    except Exception as e:
        warnings.warn(f"[BondGraphAPIWarn] Some error occurs when BUILDing the bond-graph model : {e}.")
        return jsonify(None)

@ app.route('/bond-graph/step-simu-task',methods=['POST'])
def step_simu_task():
    """ POST
    [DATA]
        `modelUid`: str, 键合图模型标记
        `dataFrame`: str, JSON化后的模型
        `timeStamp`: float, 前端传入的时间戳
    [RETURN]
        'simuResult': object, 步进仿真结果
    """
    try:
        modelUid = request.form.get('modelUid')
        _dataFrame = json.loads(request.form.get('dataFrame'))
        _timeStamp = float(request.form.get('timeStamp'))
        _dataFrame['crafttime'] = _timeStamp
        simuResult = simulate_result(MODELMANAGER.get_model(modelUid), _dataFrame)
        simuResult = {k: float(v) for k, v in simuResult.items()}
        return json.dumps(simuResult).replace('NaN', 'null')
    except Exception as e:
        warnings.warn(f"[BondGraphAPIWarn] Some error occurs when CALCULATing the bond-graph model(#{modelUid}) : {e}.")
        return jsonify(None)

@ app.route('/bond-graph/restart-simu-task',methods=['POST'])
def restart_simu_task(kw=""):
    """ POST
    [DATA]
        `modelUid`: str, 键合图模型标记
    [RETURN]
        result: bool, 注销成功与否
    """
    try:
        modelUid = request.form.get('modelUid')
        MODELMANAGER.restart_model(modelUid)
        return jsonify(True)
    except Exception as e:
        warnings.warn(f"[BondGraphAPIWarn] Some error occurs when RESTARTing the bond-graph model(#{modelUid}) : {e}.")
        return jsonify(False)

@ app.route('/bond-graph/del-simu-task',methods=['POST'])
def del_simu_task(kw=""):
    """ POST
    [DATA]
        `modelUid`: str, 键合图模型标记
    [RETURN]
        result: bool, 注销成功与否
    """
    try:
        modelUid = request.form.get('modelUid')
        MODELMANAGER.del_model(modelUid)
        return jsonify(True)
    except Exception as e:
        warnings.warn(f"[BondGraphAPIWarn] Some error occurs when DELETing the bond-graph model(#{modelUid}): {e}.")
        return jsonify(False)

if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description="IP:Port Configutation")
    parser.add_argument("--addr", type=str, default="192.168.0.83") ## IP地址
    parser.add_argument("--port", type=int, default=8000) ## 端口号
    args = parser.parse_args()
    app.run(host=args.addr, port=args.port)
    del MODELMANAGER
