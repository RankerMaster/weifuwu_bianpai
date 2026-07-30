from flask import Flask, request, jsonify
import json
import datetime
import sys

sys.path.append('./IntelligentComponent/')
import model

app = Flask(__name__)

@app.route("/dataDriven/ruptureSize", methods=["POST"])
def ruptureSize():
    #默认返回内容
    return_dict = {'return_code':'RSP001', 'return_info':'核电站1#蒸汽发生器管壁泄露尺寸大小', 'result':None}

    # 判断传入的json数据是否为空
    if len(request.get_data()) == 0:
        return_dict['return_code'] = '5004'
        return_dict['return_info'] = '请求参数为空'
        return json.dumps(return_dict, ensure_ascii=False)

    req_data = json.loads(request.data)
    X_timeserise = req_data['data']
    req_data = {"input_series": X_timeserise}
    out = model.infer(req_data)
    return_dict['result'] = out

    return json.dumps(return_dict, ensure_ascii=False)

if __name__ == '__main__':
    app.run(host='192.168.137.37', port=8088)
