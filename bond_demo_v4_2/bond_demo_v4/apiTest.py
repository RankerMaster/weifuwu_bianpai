import requests, json

def data_post(url, dataframe):
    # 发送 POST 请求，返回一个包含服务器响应信息的 response 对象
    response = requests.post(url = url, data = dataframe)
    
    if response.status_code == 200:
        try:
            return response.json()
        except:
            return response.text
    else:
        raise Exception('URL未正常响应请求')

if __name__ == "__main__":
    ## 数据
    import pandas as pd
    timeIds, data = zip(*list(pd.read_csv(r".\demo\蒸汽发生器键合图模型测试数据.csv", index_col=0, header=0, encoding="gbk").T.to_dict().items()))
    with open(r".\demo\蒸汽发生器键合图模型.json", "r+", encoding="utf-8") as f:
        structJson = f.read()
    
    modelUid = data_post("http://192.168.0.83:8000/bond-graph/build-simu-task", {
            "modelStruct": structJson
        })
    print("modelUid:", modelUid)

    L = len(data)
    for i in range(L):
        stepRes = data_post("http://192.168.0.83:8000/bond-graph/step-simu-task", {
                "modelUid": modelUid,
                "dataFrame": json.dumps(data[i]),
                "timeStamp": json.dumps(timeIds[i])
            })
        print(f"stepRes[{i}]:", stepRes)

    resState = data_post("http://192.168.0.83:8000/bond-graph/restart-simu-task", {
            "modelUid": modelUid
        })
    print("resState:", resState)

    for i in range(L):
        stepRes = data_post("http://192.168.0.83:8000/bond-graph/step-simu-task", {
                "modelUid": modelUid,
                "dataFrame": json.dumps(data[i]),
                "timeStamp": json.dumps(timeIds[i])
            })
        print(f"stepRes[{i}]:", stepRes)
    
    delState = data_post("http://192.168.0.83:8000/bond-graph/del-simu-task", {
            "modelUid": modelUid
        })
    print("delState:", delState)
