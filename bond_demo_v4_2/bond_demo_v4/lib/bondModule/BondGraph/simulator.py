import time, uuid, pickle, math
from datetime import datetime
import pandas as pd
from typing import Iterable, Dict
from .utils.solve import Solver, _fsolve
from .utils.interpolate import InterPolator
import os, json
from . import components
from .components import *

def _getComponent(compname):
    modulename, compmodelname = compname.split("/", 1)
    return getattr(getattr(components, modulename), compmodelname)

def _get_value(value,isFunc=None):#, funcData=None):
    if not isFunc:
        try:
            return float(value)
        except:
            return value
    else:
        """
        if funcData:
            data = pd.DataFrame(funcData, header=0, index_col=0)
            if data.shape[1] <= 1:
                return InterPolator([data.index.values, data.values.flatten()]).predict
            elif data.shape[0] <= 1:
                return InterPolator([data.columns.values, data.values.flatten()]).predict
            return InterPolator([data.columns.values, data.index.values, data.values]).predict
        """
        if isinstance(value, Dict) and "ndim" in value.keys():
            if value["ndim"] == 1:
                return InterPolator([value.get("x"), value.get("y")]).predict
            return InterPolator([value.get("x1"), value.get("x2"), value.get("y")]).predict
        else:
            return None
               

def wash_info(item):
    item["inputs"] = {"e1":[], "f1":[], "e2":[], "f2":[]}
    item["showConfig"] = item.get("showConfig", {})
    item["showConfig"]["properties"] = item["showConfig"].get("properties", {})
    item["showConfig"]["properties"]["scopedata"] = []
    return item

def _round(num):
    if math.isnan(num):
        return "NaN"
    else:
        return f"{num: .3e}"

class simulateManager:
    def __init__(self, struct={}, name="simulation",
                 h=0.01, maxfev=2000, how="r-k4", n=5,
                 no_build=False, uid=None,
                 #paramFiles=None, 
                 warm_start=False):
        self._args_comp = []
        self._init_state = None
        if not warm_start:
            self._name = name
            self.t_pre = None
            self._uuid = uid or str(uuid.uuid3(uuid.NAMESPACE_DNS, f"{name}-{round(time.time()*100000)}"))
            self._timeData = [0]

            self._solver = Solver(h=h, maxfev=maxfev, how=how, n=n)
            
            self._components, self._edges = struct["nodes"], struct["edges"]
            
            _compUuid = [comp["id"] for comp in self._components]
            self._scopeMap = {}
            self._pname = {}
            self._components = [wash_info(comp) for comp in self._components]
            
            for edgeInfo in sorted(self._edges, key=lambda itm: itm.get("showConfig", {}).get("startAnchor", {}).get("y", 0)):
                srcUid = edgeInfo["from"]
                trgUid = edgeInfo["to"]
                srcId = _compUuid.index(srcUid)
                self._components[srcId]["inputs"]["e2"].append(f"{srcUid}_{trgUid}_e_{edgeInfo['id']}")
                self._components[srcId]["inputs"]["f2"].append(f"{srcUid}_{trgUid}_f_{edgeInfo['id']}")
        
            edgeIds = sorted(range(len(self._edges)), key=lambda itm_id: self._edges[itm_id].get("showConfig", {}).get("endAnchor", {}).get("y", 0))
            for edgeId  in edgeIds:
                edgeInfo = self._edges[edgeId]
                srcUid = edgeInfo["from"]
                trgUid = edgeInfo["to"]
                trgId = _compUuid.index(trgUid)
                self._components[trgId]["inputs"]["e1"].append(f"{srcUid}_{trgUid}_e_{edgeInfo['id']}")
                self._components[trgId]["inputs"]["f1"].append(f"{srcUid}_{trgUid}_f_{edgeInfo['id']}")
                self._edges[edgeId]["key"] = {
                    "e": f"{srcUid}_{trgUid}_e_{edgeInfo['id']}",
                    "f": f"{srcUid}_{trgUid}_f_{edgeInfo['id']}",
                    }
                if "-detect" in self._components[trgId]["type"]:
                    if "-e-" in "-"+self._components[trgId]["type"]+"-":
                        self._scopeMap[f"{srcUid}_{trgUid}_e_{edgeInfo['id']}"] = trgId
                        self._pname[f"{srcUid}_{trgUid}_e_{edgeInfo['id']}"] = self._components[trgId]["text"]
                        self._components[trgId]["showConfig"]["properties"]["scopedata"].append({"label": "势", "data": []})
                    elif "-f-" in "-"+ self._components[trgId]["type"]+"-":
                        self._scopeMap[f"{srcUid}_{trgUid}_f_{edgeInfo['id']}"] = trgId
                        self._pname[f"{srcUid}_{trgUid}_f_{edgeInfo['id']}"] = self._components[trgId]["text"]
                        self._components[trgId]["showConfig"]["properties"]["scopedata"].append({"label": "流", "data": []})
                
            self._bondComponents = []
            self._sourceMap = {}
            for compInd, compInfo in enumerate(self._components):
                """
                if compInfo["showConfig"]["properties"]["componentName"].lower().endswith("source"):
                    if "-e-" in f"-{compInfo['type']}-":
                        self._sourceMap[compInfo['text']] = [f"source_{ename}" for ename in compInfo["inputs"]["e1"] + compInfo["inputs"]["e2"]]
                    elif "-f-" in f"-{compInfo['type']}-":
                        self._sourceMap[compInfo['text']] = [f"source_{fname}" for fname in compInfo["inputs"]["f1"] + compInfo["inputs"]["f2"]]
                    else:
                        self._sourceMap[compInfo['text']] = [f"source_{ename}" for ename in compInfo["inputs"]["e1"] + compInfo["inputs"]["e2"]] + [f"source_{fname}" for fname in compInfo["inputs"]["f1"] + compInfo["inputs"]["f2"]]
                """
                if "-global-detect-" in "-"+compInfo["type"]+"-" and len(compInfo["showConfig"]["properties"].get("params", [])) > 0:
                    paraInfo_ = compInfo["showConfig"]["properties"]["params"][0]
                    self._scopeMap[f"GLOBAL_{paraInfo_['value']}"] = _compUuid.index(compInfo["id"])
                    self._pname[f"GLOBAL_{paraInfo_['value']}"] = paraInfo_["value"]
                    self._components[compInd]["showConfig"]["properties"]["scopedata"].append({"label": paraInfo_['value'], "data": []})  
                    continue

                elif  "isFold" in compInfo.get("showConfig", {}).get("properties", {}).keys():
                    continue

                _params = {}
                for infoId, info in enumerate(compInfo["showConfig"]["properties"].get("params", [])):
                    if not info.get("isGlobal"):
                        if  info.get("isFunc"):
                            _params[info["key"]] =  _get_value(info["value"], True)
                        else:
                            value_ =  _get_value(info["value"], False)
                            if isinstance(value_, str):
                                self._sourceMap[value_] = self._sourceMap.get(value_, []) + [info["key"]]
                            else:
                                _params[info["key"]] =  value_

                compInfo["inputs"]["global"] = [f"GLOBAL_{info['value']}" for infoId, info in enumerate(compInfo["showConfig"]["properties"].get("params", [])) if info.get("isGlobal")]

                self._bondComponents.append(
                    # self._global_names |= set(compInfo["inputs"]["global"])
                    _getComponent(compInfo["showConfig"]["properties"]["componentName"])(
                        name=compInfo["text"],
                        uid=compInfo["id"],
                        input_name=compInfo["inputs"],
                        params_name = [f"{compInfo['id']}_params_{pinfo['key']}" for pinfo in compInfo["showConfig"]["properties"].get("params", [])
                                       if not pinfo.get('isGlobal')],
                        params=_params
                        )
                    )
            
            print(F"BOND - - [{datetime.now().strftime('%Y/%m/%d %H:%M:%S')}] \"INFO | Succeed to Prepare the information to build the given model\" 200 -")

            if not no_build:
                self.build()

    def build(self):
        self._args_comp = []
        for comp in self._bondComponents:
            cnt = self._solver.add_equation(comp.equation)
            for argind in range(cnt):
                self._args_comp.append([comp._name, argind])
        self._solver.build()
        self._timeData = [0]
        self.restart()

    def summary(self):
        print(f"Model [{self._name}]:")
        for eqind, eqloc in enumerate(self._args_comp):
            print(f"\tEquation {eqind}: {eqloc[0]}[{eqloc[1]}]")

    def restart(self):
        if self._init_state is None:
            self._init_state = _fsolve(lambda args: self._solver._equation(args, {}), [0 for _ in self._solver._input_names], maxfev=self._solver._maxfev)
        self._state = self._init_state[:]
        self.t_pre = None

    def _renderEdge(self, res):
        return [
            {
                **edgeInfo,
                "value": {
                    "e": res.get(edgeInfo["key"]["e"], float("nan")),
                    "f": res.get(edgeInfo["key"]["f"], float("nan"))
                    },
                "text": f"势:{_round(res.get(edgeInfo['key']['e'], float('nan')))}\n流:{_round(res.get(edgeInfo['key']['f'], float('nan')))}",
                } for edgeInfo in self._edges
            ]

    def _renderState(self):
        return {self._pname[n]: (self._state[nid] if not self._state is None else float("nan")) for nid, n in enumerate(self._solver._input_names) if n in self._pname.keys()}

    def _step(self, params={}, inner=True, return_state=False):
        _state, res = self._solver.solve(state=self._state, params=params)
        if inner:
            self._state = _state
        for param, val in res.items():
            if param in self._scopeMap.keys():
                compId = self._scopeMap[param]
                if isinstance(val, Iterable):
                    for vid, v in enumerate(val):
                        self._components[compId]["showConfig"]["properties"]["scopedata"][vid]["data"].append(v)
                else:
                    self._components[compId]["showConfig"]["properties"]["scopedata"][0]["data"].append(val)
                    

        self._timeData.append((self._timeData[-1]+self._solver._h))
        
        if return_state:
            return _state, {"nodes": self._components, "edges": self._renderEdge(res), "timeData": self._timeData[:-1]}
        else:
            return {"nodes": self._components, "edges": self._renderEdge(res), "timeData": self._timeData[:-1]}

    def refresh(self, params={}):
        return self._step({k_: v for k, v in params.items() for k_ in self._sourceMap.get(k, [])})

    def save(self):
        return json.dumps({
                "name": self._name, "uuid": str(self._uuid),
                "struct": {"nodes": self._components, "edges": self._edges},
                "scopeMap": self._scopeMap, "pname": self._pname,
                "sourceMap": self._sourceMap,
                #"paramFiles": self._paramFiles,
                "t_pre": float(self.t_pre) if not self.t_pre is None else None,
                "state": [float(s) for s in self._state] if not self._state is None else None,
                "init_state": [float(s) for s in self._init_state] if not self._init_state is None else None
            })

    @staticmethod
    def load(confJson, restart=True):
        smltInfo = json.loads(confJson)
        if "struct" in smltInfo.keys():
            smlt = simulateManager(warm_start=True)
            smlt._name = smltInfo.get("name", "")
            smlt._init_state = smltInfo.get("init_state")
            smlt.t_pre = smltInfo.get("t_pre") if not restart else None
            smlt._timeData = [0]
            smlt._uuid = smltInfo.get("uuid", str(uuid.uuid3(uuid.NAMESPACE_DNS, f"{smlt._name}-{round(time.time()*100000)}")))
            smlt._components = smltInfo["struct"]["nodes"]
            smlt._edges = smltInfo["struct"]["edges"]
            smlt._scopeMap = smltInfo.get("scopeMap", {})
            smlt._pname = smltInfo.get("pname", {})
            smlt._sourceMap = smltInfo.get("sourceMap", {})
            smlt._solver = Solver(h=0.01, maxfev=2000, how="r-k4", n=5)
            #smlt._paramFiles = smltInfo.get("paramFiles")
            smlt._bondComponents = [
                _getComponent(compInfo["showConfig"]["properties"]["componentName"])(
                    name=compInfo["text"],
                    uid=compInfo["id"],
                    input_name=compInfo["inputs"],
                    params={info["key"]: _get_value(info["value"], info.get("isFunc"),
                                                    # (smlt._paramFiles or {}).get(f"{compInfo['id']}{infoId}")
                                                    ) for infoId, info in enumerate(compInfo["showConfig"]["properties"].get("params", []))}
                ) for compInfo in smlt._components]
            smlt.build()
            smlt._state = smltInfo.get("state") if not restart else None
        else:
            smlt = simulateManager(smltInfo, uid=str(uuid.uuid3(uuid.NAMESPACE_DNS, f"bond-graph-simulation-{round(time.time()*100000)}")))
        return smlt
