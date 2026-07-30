from flask import Flask, request, render_template, jsonify, send_file
#from flask_cors import CORS

app = Flask(__name__, template_folder="./templates", static_folder="./static", static_url_path='/static')

app.jinja_env.auto_reload = True
app.config['TEMPLATES_AUTO_RELOAD'] = True
app.jinja_env.variable_start_string = '||<-EL PSY CONGROO->||'
app.jinja_env.variable_end_string = '||<-EL PSY CONGROO->||'

from lib.bondModule.bondApp import bond_route_app

bond_route_app(app)

@ app.route('/',methods=['GET', 'POST'])
def bond_panel_init():
    return render_template("bond-panel.html")
    
if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description="IP:Port Configutation")
    parser.add_argument("--addr", type=str, default="192.168.0.83")
    parser.add_argument("--port", type=int, default=8050)
    args = parser.parse_args()
    app.run(host=args.addr, port=args.port)#, debug=True)
