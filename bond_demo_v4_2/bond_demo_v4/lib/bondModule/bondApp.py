import json, time
import pandas as pd
from flask import request
from .BondGraph.simulator import simulateManager
from datetime import datetime
import traceback

def result_analyse_main():
    print(f"BOND - - [{datetime.now().strftime('%Y/%m/%d %H:%M:%S')}] \"INFO | Get the simulation task\" 200 -")
    try:
        data = [{"crafttime": crafttime, **itm} for crafttime, itm in pd.read_csv(request.files.get("dataFile"), header=0, encoding="gbk").T.to_dict().items()]
        timeIds = [itm["crafttime"] for itm in data]
        h_ = (timeIds[-1]-timeIds[0])/(len(timeIds)-1)
    except:
        print(f"BOND - - [{datetime.now().strftime('%Y/%m/%d %H:%M:%S')}] \"ERROR | Failed to Load the given source data\" 500 -")
        traceback.print_exc()
        raise Exception("[Bond Simulation Error] Failed to Load the given source data") 
    print(F"BOND - - [{datetime.now().strftime('%Y/%m/%d %H:%M:%S')}] \"INFO | Succeed to Build the given source data\" 200 -")
    
    try:
        struct = json.loads(request.form.get("graphStruct"))
        smlt = simulateManager(struct, h=h_)#, paramFiles=request.files)
    except:
        print(f"BOND - - [{datetime.now().strftime('%Y/%m/%d %H:%M:%S')}] \"ERROR | Failed to Build the given model\" 500 -")
        traceback.print_exc()
        raise Exception("[Bond Simulation Error] Failed to Build the given model") 
    print(F"BOND - - [{datetime.now().strftime('%Y/%m/%d %H:%M:%S')}] \"INFO | Succeed to Build the given model\" 200 -")
    
    try:
        t_s = time.time(); t_step = 120
        process_act = 0; process_step = 1/len(data)
        smlt.t_pre = timeIds[0]
        for itm, t_pre, t_act in zip(data, timeIds, timeIds[1:]+[timeIds[-1]+h_]):
            process_act += process_step
            if t_pre == t_act:
                continue
            smlt._solver.set_h(t_act-smlt.t_pre)
            result = smlt.refresh(itm)
            smlt.t_pre  = t_act
            if time.time() - t_s > t_step:
                t_s = time.time()
                print(f"BOND - - [{datetime.now().strftime('%Y/%m/%d %H:%M:%S')}] \"INFO | Finish {round(process_act*100, 2)}%\" 200 -")
        print(result)
    except:
        print(f"BOND - - [{datetime.now().strftime('%Y/%m/%d %H:%M:%S')}] \"ERROR | Failed at {t_act}s\" 500 -")
        traceback.print_exc()
        raise Exception(f"[Bond Simulation Error] Failed at {t_act}s") 
    print(F"BOND - - [{datetime.now().strftime('%Y/%m/%d %H:%M:%S')}] \"INFO | Succeed to Finish the simulation\" 200 -")
    
    return json.dumps(result).replace("NaN", "null")


def export_config_main():
    print(f"BOND - - [{datetime.now().strftime('%Y/%m/%d %H:%M:%S')}] \"INFO | Get the export task\" 200 -")
    try:
        struct = json.loads(request.form.get("graphStruct"))
        smlt = simulateManager(struct)#, paramFiles=request.files)
    except:
        print(f"BOND - - [{datetime.now().strftime('%Y/%m/%d %H:%M:%S')}] \"ERROR | Failed to Build the given model\" 500 -")
        traceback.print_exc()
        raise Exception("[Bond Simulation Error] Failed to Build the given model") 
    print(F"BOND - - [{datetime.now().strftime('%Y/%m/%d %H:%M:%S')}] \"INFO | Succeed to Build the given model\" 200 -")
    
    return smlt.save()

def convert_csv():
    data = pd.read_csv(request.files.get("csvFile"), header=0, index_col=0).astype("float32")
    if data.shape[1] <= 1:
        return json.dumps({"x": data.index.values.astype(float).tolist(), "y":data.values.flatten().astype(float).tolist(), "ndim": 1}).replace("NaN", "null")
    elif data.shape[0] <= 1:
        return json.dumps({"x": data.columns.values.astype(float).tolist(), "y":data.values.flatten().astype(float).tolist(), "ndim": 1}).replace("NaN", "null")
    return json.dumps({"x1": data.columns.values.astype(float).tolist(), "x2": data.index.values.astype(float).tolist(), "y":data.values.astype(float).tolist(), "ndim": 2}).replace("NaN", "null")

def bond_route_app(app):
    app.route("/bond-analyse/analyse-data/",methods=['POST'])(result_analyse_main)
    app.route("/bond-analyse/export-config-complet/",methods=['POST'])(export_config_main)
    app.route("/bond-analyse/convert-csv-file/",methods=['POST'])(convert_csv)
    
